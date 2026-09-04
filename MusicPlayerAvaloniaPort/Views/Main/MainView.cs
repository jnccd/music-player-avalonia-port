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
    }

    void SetupUi()
    {
        // Song Setup Thread (so it doesnt block the UI)
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000 / 45),
            DispatcherPriority.Background,
            Dispatcher.UIThread);
        timer.Tick += (s, e) =>
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
        timer.Start();
        Task.Run((Action)(() =>
        {
            Thread.CurrentThread.Name = "SongSetupThread";

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
            }));
        }));

        // Events
        Window?.Closing += MainView_Closing;
        Window?.ScalingChanged += MainView_ScalingChanged;
        songPlaybackService.NewSongStarted += (s, song) => UpdateUiForNewSong(song);
        songPlaybackService.UpvoteLockedInChanged += (s, lockedIn) => UpdateUiForNewUpvoteLockedInState(lockedIn);
        this.AddHandler(
            InputElement.KeyDownEvent,
            UserControl_KeyDown,
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

    private void UserControl_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V)
        {
            CustomRenderControl_Diagram_Getter.CycleVisMode();
        }
    }
}