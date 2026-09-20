using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Views.Options;
using MusicPlayerAvaloniaPort.Views.Statistics;

namespace MusicPlayerAvaloniaPort.Views.Main;

public partial class MainView : UserControl
{
    Window? Window => TopLevel.GetTopLevel(this) as Window;
    MainViewModel? ViewModel => DataContext as MainViewModel;

    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    readonly SongChoosingService songChoosingService = ServiceContainer.GetService<SongChoosingService>();
    readonly SongVotingService songVotingService = ServiceContainer.GetService<SongVotingService>();
    readonly SongVolumeService songVolumeService = ServiceContainer.GetService<SongVolumeService>();
    readonly AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    readonly MprisService? mprisService = ServiceContainer.TryGetService<MprisService>();
    /// <summary>
    /// Resolved by the background song-setup thread (see <see cref="SetupUi"/>) instead of at view
    /// construction: the SongSyncService constructor performs a network init, which must not run on
    /// the UI thread. Its <see cref="SongSyncService.SyncProgress"/> drives the sync segment of the
    /// startup progress bar.
    /// </summary>
    volatile SongSyncService? songSyncService;

    const double MAX_VOLUME = 1;

    /// <summary>
    /// Startup progress bar timer (see <see cref="SetupUi"/>); kept in a field so it can be stopped once the
    /// startup finished instead of polling progress forever.
    /// </summary>
    DispatcherTimer? startupProgressTimer;
    /// <summary>True once the song library is set up, i.e. the startup bar is gone and playback may start.</summary>
    bool startupLibraryReady;

    /// <summary>
    /// Delay before the loading indicator of a song change is shown. Starting a song opens its file on the
    /// song library, which can be a NAS that has to spin up first, so a switch can take noticeably long; on
    /// a local library it finishes in a few milliseconds and the indicator never appears (so the UI does not
    /// flicker on every skip).
    /// </summary>
    static readonly TimeSpan SongChangeLoadingIndicatorDelay = TimeSpan.FromMilliseconds(250);
    DispatcherTimer? songChangeLoadingIndicatorTimer;
    bool songChangeLoadingIndicatorShown;

    CustomRenderControl_Diagram CustomRenderControl_Diagram_Getter => this.GetLogicalDescendants().OfType<CustomRenderControl_Diagram>().FirstOrDefault()!;
    CustomRenderControl_PlayProgress CustomRenderControl_PlayProgress_Getter => this.GetLogicalDescendants().OfType<CustomRenderControl_PlayProgress>().FirstOrDefault()!;
    CustomRenderControl_Title CustomRenderControl_Title_Getter => this.GetLogicalDescendants().OfType<CustomRenderControl_Title>().FirstOrDefault()!;
    CustomRenderControl_Notification CustomRenderControl_Notification_Getter => this.GetLogicalDescendants().OfType<CustomRenderControl_Notification>().FirstOrDefault()!;

    ProgressBar ProgressBarInit_Getter => this.GetLogicalDescendants().OfType<ProgressBar>().FirstOrDefault(x => x.Name == "ProgressBarInit")!;

    public MainView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);

        // Events
        this.Loaded += MainView_Loaded;
    }

    private void MainView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("MainView loaded!");

        SetupUi();
        SubscribeKeyboardFocusRestore();
    }

    void SetupUi()
    {
        // Song Setup Thread (so it doesnt block the UI)
        startupProgressTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000 / 45),
            DispatcherPriority.Background,
            Dispatcher.UIThread);
        startupProgressTimer.Tick += (s, e) =>
        {
            // Bar composition over the whole startup: the startup sync pull (SongSyncService.SyncProgress,
            // 0..1 coarse milestones) gets the first quarter of the bar; the song library setup - the
            // scan + choosing-data-structure progress composite, at most 100 + 33 - fills the remaining
            // three quarters.
            float syncPart = (songSyncService?.SyncProgress ?? 0) * 25;
            float libraryPart = (songPlaybackService.UpdateAvailableSongPathsProgress * 100
                + songChoosingService.CreateSongChoosingDataStructureProgress * 33) * (75f / 133f);
            ProgressBarInit_Getter.Value = syncPart + libraryPart;
        };
        startupProgressTimer.Start();
        Task.Run((Action)(() =>
        {
            Thread.CurrentThread.Name = "SongSetupThread";

            // Schema first: the database lives in the user's data directory now (see PersistenceLocations)
            // and does not exist yet on a fresh machine/user, so the app creates and migrates it itself
            // before anything reads it - the sync service resolved right below already queries it in its
            // constructor. This replaces the "dotnet ef database update" step of start_desktop_app.sh.
            ServiceContainer.GetService<DbWrapperService>().EnsureDatabaseUpToDate();

            // Resolve the sync service on this background thread (its constructor does a network init):
            // the progress bar above polls its SyncProgress while the StartupSync pull below runs.
            songSyncService = ServiceContainer.GetService<SongSyncService>();

            // Song library setup: first resolve the folder (config, env var or folder picker), then pull
            // the latest data from the sync server (the folder has to be known for the account check and
            // the migration application, see StartupSync()), and only then scan the library, so the file
            // names, the database rows and the applied migrations line up. Afterwards playback can start.
            ResolveSongLibraryPath();
            StartupSync();
            ScanSongLibrary();

            songPlaybackService.GetNextSong();

            mprisService?.Init();

            Dispatcher.UIThread.InvokeAsync((Action)(() =>
            {
                ProgressBarInit_Getter.Value = 100;
                ProgressBarInit_Getter.IsVisible = false;
                // The startup bar is done: stop polling it and let the song-change loading indicator (see
                // OnSongChangeStarted) take over the same control from here on.
                startupProgressTimer?.Stop();
                startupLibraryReady = true;
            }));
        }));

        // Events
        Window?.Closing += MainView_Closing;
        Window?.ScalingChanged += MainView_ScalingChanged;
        songPlaybackService.NewSongStarted += (s, song) => UpdateUiForNewSong(song);
        songPlaybackService.SongChangeStarted += (s, e) => Dispatcher.UIThread.Post(OnSongChangeStarted);
        songPlaybackService.SongChangeFinished += (s, e) => Dispatcher.UIThread.Post(OnSongChangeFinished);
        songPlaybackService.UpvoteLockedInChanged += (s, lockedIn) => UpdateUiForNewUpvoteLockedInState(lockedIn);
        this.AddHandler(
            InputElement.KeyDownEvent,
            UserControl_KeyDown,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        this.AddHandler(
            InputElement.KeyUpEvent,
            UserControl_KeyUp,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        audioLibWrapper.PlaybackStateChanged += (e, s) =>
        {
            RefreshCustomControls();
        };
        songVotingService.SongGotUpvoted += (s, e) =>
        {
            Dispatcher.Post(() =>
            {
                CustomRenderControl_Notification_Getter.ShowUpvoteNotif();
            });
        };
        songVotingService.SongGotDownvoted += (s, e) =>
        {
            Dispatcher.Post(() =>
            {
                CustomRenderControl_Notification_Getter.ShowDownvoteNotif();
            });
        };
        // Repaint the upvote button when its accent color is changed while it is locked in.
        ThemeColors.PrimaryColorChanged += UpdateButtonUpvoteColor;

        // Inits
        MainView_ScalingChanged(null, EventArgs.Empty);
        LoadVolume();

        // Visuals
        // On Windows disable the extra border resizing feature since the OS border is active (see AvaloniaWindowManager.cs)
        var cosmeticBorder = this.GetLogicalDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "CosmeticBorder");
        if (OperatingSystem.IsWindows())
        {
            cosmeticBorder!.BorderBrush = new SolidColorBrush(Color.Parse("#00000000"));

            var resizeWindowBorder = this.GetLogicalDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "ResizeWindowBorder");
            resizeWindowBorder?.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
        }
        else
        {
            cosmeticBorder!.BorderThickness = new Avalonia.Thickness(1 / CurrentRenderScaling());
        }
    }

    /// <summary>
    /// Shows the loading indicator for the song change that just started - but only when it is still running
    /// after <see cref="SongChangeLoadingIndicatorDelay"/>. Reuses the startup progress bar (hidden by then)
    /// in its indeterminate look, since the length of the wait is unknown. Nothing is shown for a fast
    /// switch, so the UI does not flicker on every skip.
    /// </summary>
    void OnSongChangeStarted()
    {
        if (!startupLibraryReady || songChangeLoadingIndicatorShown)
            return;

        if (songChangeLoadingIndicatorTimer == null)
        {
            songChangeLoadingIndicatorTimer = new DispatcherTimer(SongChangeLoadingIndicatorDelay, DispatcherPriority.Background, Dispatcher.UIThread);
            songChangeLoadingIndicatorTimer.Tick += (s, e) =>
            {
                songChangeLoadingIndicatorTimer!.Stop();
                var progressBar = ProgressBarInit_Getter;
                progressBar.IsIndeterminate = true;
                progressBar.IsVisible = true;
                songChangeLoadingIndicatorShown = true;
            };
        }

        // Restart the delay for this switch (the previous one may still be pending).
        songChangeLoadingIndicatorTimer.Stop();
        songChangeLoadingIndicatorTimer.Start();
    }

    /// <summary>
    /// Hides the loading indicator of a finished song change - or cancels it entirely when the switch was
    /// quicker than <see cref="SongChangeLoadingIndicatorDelay"/>.
    /// </summary>
    void OnSongChangeFinished()
    {
        songChangeLoadingIndicatorTimer?.Stop();
        if (!songChangeLoadingIndicatorShown)
            return;

        songChangeLoadingIndicatorShown = false;
        var progressBar = ProgressBarInit_Getter;
        progressBar.IsVisible = false;
        progressBar.IsIndeterminate = false; // Restore the determinate startup look of the shared bar
    }

    void RefreshCustomControls()
    {
        Dispatcher.UIThread.InvokeAsync(CustomRenderControl_Diagram_Getter.InvalidateVisual, DispatcherPriority.Background);
        Dispatcher.UIThread.InvokeAsync(CustomRenderControl_PlayProgress_Getter.InvalidateVisual, DispatcherPriority.Background);
        Dispatcher.UIThread.InvokeAsync(CustomRenderControl_Title_Getter.InvalidateVisual, DispatcherPriority.Background);
    }

    void ButtonOptions_Click(object? sender, RoutedEventArgs e)
    {
        AvaloniaWindowManager.ShowWindow(typeof(OptionsView));
    }

    void ButtonStatistics_Click(object? sender, RoutedEventArgs e)
    {
        AvaloniaWindowManager.ShowWindow(typeof(StatisticsView));
    }

    private void MainView_Closing(object? sender, WindowClosingEventArgs e)
    {
        Config.Save();
        Debug.WriteLine("MainView closing!");
    }

    private void MainView_ScalingChanged(object? sender, EventArgs e)
    {

    }

    private void MainView_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        CustomRenderControl_Diagram_Getter.UpdateDiagramScaling();

        RefreshCustomControls();
    }

    void ButtonClose_Click(object? sender, RoutedEventArgs e)
    {
        Window?.Close();
        Environment.Exit(0);
    }

    bool keyboardFocusRestoreSubscribed;

    /// <summary>
    /// Avalonia only raises key events for the control that holds keyboard focus. While nothing inside
    /// the main window has focus (before the first click on the view), and again whenever the window
    /// regains activation without an inner focus (e.g. after coming back from the options/statistics
    /// window), the V/K hotkeys handled in <see cref="UserControl_KeyDown"/> never fire. This keeps the
    /// keyboard focus on this root view instead - it is focusable (see the axaml) but has no activation
    /// behavior of its own, so no accidental button-like side effects.
    /// </summary>
    void SubscribeKeyboardFocusRestore()
    {
        if (keyboardFocusRestoreSubscribed)
            return;
        keyboardFocusRestoreSubscribed = true;

        // Opened covers the startup case, Activated the "came back to the window" case (and retries the
        // startup focus if the window was not active yet when Opened fired - focusing an inactive window
        // is ignored).
        Window?.Opened += (s, e) => RestoreKeyboardFocusIfLost();
        Window?.Activated += (s, e) => RestoreKeyboardFocusIfLost();
    }

    void RestoreKeyboardFocusIfLost()
    {
        if (!IsKeyboardFocusWithin)
            Focus();
    }

    private void UserControl_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V)
        {
            CustomRenderControl_Diagram_Getter.CycleVisMode();
        }
    }

    private void UserControl_KeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // The DxMGP client opened its console with K, where a typed song name was matched and played
        // (see MainView_SongLogic.QuickPlaySong). This port has no console, so K runs the same flow
        // through message boxes instead. Quick play is triggered on KEY UP on purpose: the dialog
        // focuses its text box as soon as it opens, and the character of the opening key press would
        // otherwise still be delivered after the focus switch (a stray "k" typed into the fresh
        // dialog). It also keeps a held-down K from auto-repeating keys into the text box.
        if (e.Key == Key.K)
        {
            QuickPlaySong();
        }
    }
}