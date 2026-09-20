using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.Views.ExportLibrary;
using MusicPlayerAvaloniaPort.Views.History;
using MusicPlayerAvaloniaPort.Views.Statistics;
using MusicPlayerAvaloniaPort.Views.Wrapped;
using MusicPlayerAvaloniaPort.Services.Wrapped;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.Media;

namespace MusicPlayerAvaloniaPort.Views.Options;

public partial class OptionsView : UserControl
{
    Window? window => TopLevel.GetTopLevel(this) as Window;

    readonly SongSyncService syncService = ServiceContainer.GetService<SongSyncService>();
    readonly SongDownloadRequestProcessorService songDownloadRequestProcessorService = ServiceContainer.GetService<SongDownloadRequestProcessorService>();
    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    readonly SystemAudioCaptureService systemAudioCaptureService = ServiceContainer.GetService<SystemAudioCaptureService>();
    readonly WrappedService wrappedService = ServiceContainer.GetService<WrappedService>();

    // Primary color picker (see the General group); resolved when the view is loaded. Deliberately not
    // named like the controls in the axaml: the Avalonia name generator already declares fields for those,
    // but they are only filled by the generated InitializeComponent, which this view does not call.
    Border? colorPreviewBorder;
    TextBox? colorHexTextBox;
    ColorPickerControl? colorPickerControl;
    /// <summary>
    /// True while <see cref="UpdatePrimaryColorUi"/> writes the controls, so the text box' TextChanged
    /// handler does not mistake the programmatic text for something the user typed.
    /// </summary>
    bool updatingPrimaryColorUi;

    // System audio visualization toggle (see the General group); resolved when the view is loaded, for the
    // same reason as the color controls above.
    CheckBox? systemAudioCaptureToggle;
    TextBlock? systemAudioCaptureStateText;
    /// <summary>
    /// True while <see cref="UpdateSystemAudioCaptureUi"/> writes the toggle, so its IsCheckedChanged
    /// handler does not mistake the programmatic state for a user click.
    /// </summary>
    bool updatingSystemAudioCaptureUi;
    bool subscribedToSystemAudioCaptureState;

    public OptionsView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);

        // Events
        this.Loaded += OptionsView_Loaded;
    }

    private void OptionsView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("OptionsView loaded!");

        Dispatcher.Invoke(() =>
        {
            if (window == null)
                throw new InvalidDataException(nameof(window));

            window.MinWidth = double.IsNormal(window.Width) ? window.Width : 0;
            window.MinHeight = double.IsNormal(window.Height) ? window.Height : 0;
        });

        var syncStateLabel = this.GetNestedControl<TextBlock>("syncStateLabel");
        syncStateLabel?.Text = syncService.State;
        syncService.OnStateChanged = state => Dispatcher.Invoke(() => syncStateLabel?.Text = state);

        var downloadStateLabel = this.GetNestedControl<TextBlock>("downloadStateLabel");
        downloadStateLabel?.Text = songDownloadRequestProcessorService.State;
        songDownloadRequestProcessorService.OnStateChanged = state => Dispatcher.Invoke(() => downloadStateLabel?.Text = state);

        var downloadCsharpLogLabel = this.GetNestedControl<TextBox>("downloadCsharpLogLabel");
        downloadCsharpLogLabel?.Text = songDownloadRequestProcessorService.CsharpLog.Combine();
        songDownloadRequestProcessorService.CsharpLogAdded = () => Dispatcher.Invoke(() =>
            downloadCsharpLogLabel?.Text = songDownloadRequestProcessorService.CsharpLog.Combine());

        var downloadShellLogLabel = this.GetNestedControl<TextBox>("downloadShellLogLabel");
        downloadShellLogLabel?.Text = songDownloadRequestProcessorService.ShellLog.Combine();
        songDownloadRequestProcessorService.ShellAdded = () => Dispatcher.Invoke(() =>
            downloadShellLogLabel?.Text = songDownloadRequestProcessorService.ShellLog.Combine());

        InitPrimaryColorPicker();
        InitSystemAudioCapture();
        UpdateWrappedState();
    }

    /// <summary>
    /// Mirrors what the wrapped feature currently has: how many reports exist and how much of the library
    /// is already measured. The wrapped window owns the actual work; this is the at-a-glance state next to
    /// the button that opens it.
    /// </summary>
    void UpdateWrappedState()
    {
        var wrappedStateLabel = this.GetNestedControl<TextBlock>("wrappedStateLabel");
        if (wrappedStateLabel == null)
            return;

        try
        {
            int reportCount = wrappedService.ListReports().Count;
            int cached = wrappedService.CachedAnalyses;
            int years = wrappedService.GetAvailableYears().Count;

            wrappedStateLabel.Text = years == 0
                ? "No play history recorded yet."
                : $"{reportCount} report(s) saved, history for {years} year(s), audio measured for {cached} file(s).";
        }
        catch (Exception ex)
        {
            wrappedStateLabel.Text = $"Wrapped state unavailable: {ex.Message}";
        }
    }

    // ---------- Extra windows (the "Windows" group) ----------

    private void StatisticsWindowButton_Click(object? sender, RoutedEventArgs e) =>
        AvaloniaWindowManager.ShowWindow(typeof(StatisticsView));

    private void ExportLibraryWindowButton_Click(object? sender, RoutedEventArgs e) =>
        AvaloniaWindowManager.ShowWindow(typeof(ExportLibraryView));

    private void SongHistoryWindowButton_Click(object? sender, RoutedEventArgs e) =>
        AvaloniaWindowManager.ShowWindow(typeof(SongHistoryView));

    private void WrappedWindowButton_Click(object? sender, RoutedEventArgs e) =>
        AvaloniaWindowManager.ShowWindow(typeof(WrappedView));

    // ---------- Primary color (the color picker of the General group) ----------

    void InitPrimaryColorPicker()
    {
        colorPreviewBorder = this.GetNestedControl<Border>("primaryColorPreview");
        colorHexTextBox = this.GetNestedControl<TextBox>("primaryColorHexTextBox");
        colorPickerControl = this.GetNestedControl<ColorPickerControl>("primaryColorPicker");

        colorPickerControl.SetColor(ThemeColors.PrimaryColor);
        UpdatePrimaryColorUi(ThemeColors.PrimaryColor);

        colorPickerControl.ColorChanged += color =>
        {
            // Live feedback: the main window repaints while dragging, but the config is only written once
            // the drag ended (ColorChangeFinished), so a drag does not hammer the disk.
            ThemeColors.SetPrimaryColor(color, save: false);
            UpdatePrimaryColorUi(color);
        };
        colorPickerControl.ColorChangeFinished += () => Config.Save();
    }

    /// <summary>Applies a color the user entered as hex text (and mirrors it back into the picker).</summary>
    void ApplyPickedPrimaryColor(Color color)
    {
        ThemeColors.SetPrimaryColor(color);
        colorPickerControl?.SetColor(color);
        UpdatePrimaryColorUi(color);
    }

    void UpdatePrimaryColorUi(Color color)
    {
        updatingPrimaryColorUi = true;
        try
        {
            if (colorPreviewBorder != null)
                colorPreviewBorder.Background = new SolidColorBrush(color);
            if (colorHexTextBox != null)
                colorHexTextBox.Text = ThemeColors.ToHex(color);
        }
        finally
        {
            updatingPrimaryColorUi = false;
        }
    }

    private void PrimaryColorHexTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (updatingPrimaryColorUi)
            return;

        // Half-typed values ("#00", "#007B8", ...) do not parse and are simply left alone until the text
        // is a complete color.
        if (colorHexTextBox == null || !Color.TryParse(colorHexTextBox.Text, out Color color))
            return;

        ApplyPickedPrimaryColor(color);
    }

    private void ResetPrimaryColorButton_Click(object? sender, RoutedEventArgs e)
    {
        ThemeColors.ResetToDefault();
        colorPickerControl?.SetColor(ThemeColors.PrimaryColor);
        UpdatePrimaryColorUi(ThemeColors.PrimaryColor);
    }

    // ---------- System audio visualization (the toggle of the General group) ----------

    void InitSystemAudioCapture()
    {
        systemAudioCaptureToggle = this.GetNestedControl<CheckBox>("systemAudioCaptureCheckBox");
        systemAudioCaptureStateText = this.GetNestedControl<TextBlock>("systemAudioCaptureStateLabel");

        UpdateSystemAudioCaptureUi();

        // The service can change its state without this view (the persisted option is applied at startup,
        // and a start can fail), so the toggle and the state label mirror it whenever that happens. The
        // window is cached and can be shown again, hence the one-time guard.
        if (!subscribedToSystemAudioCaptureState)
        {
            subscribedToSystemAudioCaptureState = true;
            systemAudioCaptureService.StateChanged += () => Dispatcher.UIThread.Post(UpdateSystemAudioCaptureUi);
        }
    }

    void UpdateSystemAudioCaptureUi()
    {
        if (systemAudioCaptureToggle == null)
            return;

        updatingSystemAudioCaptureUi = true;
        try
        {
            systemAudioCaptureToggle.IsChecked = systemAudioCaptureService.IsEnabled;
            if (systemAudioCaptureStateText != null)
                systemAudioCaptureStateText.Text = systemAudioCaptureService.State;
        }
        finally
        {
            updatingSystemAudioCaptureUi = false;
        }
    }

    private void SystemAudioCaptureCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingSystemAudioCaptureUi)
            return;

        // Starting the capture can fail (unsupported platform, no loopback/monitor device, device in use):
        // the service then keeps the option off, and mirroring the state back below unchecks the box again
        // while the state label explains what happened.
        systemAudioCaptureService.SetEnabled(systemAudioCaptureToggle?.IsChecked == true);
        UpdateSystemAudioCaptureUi();
    }

    private void DownloadFolderSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var downloadFolderTextBox = this.GetNestedControl<TextBox>("downloadFolderTextBox");
        if (!Directory.Exists(downloadFolderTextBox.Text))
        {
            new MessageBox(_ => { }, window, this).Show("Invalid folder path", $"{downloadFolderTextBox.Text} doesn't exist!");
            return;
        }
        Config.Data.DownloadFolderPath = downloadFolderTextBox.Text;

        songDownloadRequestProcessorService.Init();
        Config.Save();
    }

    private void SelectMusicLibraryButton_Click(object? sender, RoutedEventArgs e)
    {
        var musicLibraryTextBox = this.GetNestedControl<TextBox>("musicLibraryTextBox");

        string? folder = null;
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var storageProvider = TopLevel.GetTopLevel(window)!.StorageProvider;
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select your Music Library Root Folder",
                AllowMultiple = false,
                SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(musicLibraryTextBox?.Text ?? "")
            });
            var storageFolder = folders![0];
            folder = storageFolder!.Path.AbsolutePath;

            if (folder != null)
                musicLibraryTextBox?.Text = folder;
        });
    }

    private async void SetMusicLibraryButton_Click(object? sender, RoutedEventArgs e)
    {
        var musicLibraryTextBox = this.GetNestedControl<TextBox>("musicLibraryTextBox");
        Config.Data.SongLibraryPath = musicLibraryTextBox.Text;

        if (musicLibraryTextBox?.Text != null)
        {
            // If a pull already brought song library migrations (e.g. the user logged in before setting the
            // library folder), apply the pending ones before the library scan, so the file names line up.
            // Nothing is applied when the library is registered for a different account.
            syncService.ApplySongLibraryMigrations(musicLibraryTextBox.Text);

            // If the song library is registered for a different account, nothing was applied to it. Ask the
            // user whether they want to take it over for the account of the last successful pull.
            var ownerWarning = syncService.TakeSongLibraryOwnerWarning();
            if (ownerWarning != null)
            {
                bool takeOver = await new MessageBox(e => Console.WriteLine(e), window, this)
                    .AskYesNoAsync("Song library belongs to another account",
                        ownerWarning + "\n\nDo you want to take the library over for your account?");
                if (takeOver)
                    syncService.AdoptSongLibrary(musicLibraryTextBox.Text);
            }

            songPlaybackService.UpdateAvailableSongPaths(musicLibraryTextBox.Text);
        }

        Config.Save();
    }

    /// <summary>
    /// Disables the whole sync login section and shows the loading spinner while a login+pull is running.
    /// </summary>
    private void SetLoginBusy(bool busy)
    {
        this.GetNestedControl<Button>("loginButton").IsEnabled = !busy;
        this.GetNestedControl<Button>("registerButton").IsEnabled = !busy;
        this.GetNestedControl<TextBox>("hostTextBox").IsEnabled = !busy;
        this.GetNestedControl<TextBox>("usernameTextBox").IsEnabled = !busy;
        this.GetNestedControl<TextBox>("passwordTextBox").IsEnabled = !busy;
        this.GetNestedControl<ProgressBar>("loginProgressBar").IsVisible = busy;
    }

    private async void LoginButton_Click(object? sender, RoutedEventArgs e)
    {
        var textBoxHost = this.GetNestedControl<TextBox>("hostTextBox");
        var textBoxUsername = this.GetNestedControl<TextBox>("usernameTextBox");
        var textBoxPassword = this.GetNestedControl<TextBox>("passwordTextBox");
        var syncStateLabel = this.GetNestedControl<TextBlock>("syncStateLabel");

        Config.Data.SyncServerHost = textBoxHost.Text;
        Config.Data.SyncServerUsername = textBoxUsername.Text;
        Config.Save();

        // TextBox.Text may only be read on the UI thread, so capture the values before handing
        // them to the background work.
        string password = textBoxPassword.Text ?? "";

        // Init and Pull do blocking network/DB work (and a pull can rewrite the local database and rename
        // song library files), so run them on background threads. The login button stays disabled and the
        // spinner is shown until the whole login+pull finished, keeping the UI responsive meanwhile.
        SetLoginBusy(true);
        syncStateLabel.Text = "Logging in and pulling…";
        try
        {
            try
            {
                await Task.Run(() => syncService.Init(password, true));
            }
            catch (Exception ex)
            {
                new MessageBox(e => Console.WriteLine(e), window, this)
                    .Show("Can't initialize login.", $"{syncService.State}\n\n{ex}");
                return;
            }

            try
            {
                await Task.Run(() => syncService.Pull());

                // If the song library is registered for a different account, the pull was aborted before
                // anything was synced (local database and library state file are untouched). Ask the user
                // whether they want to take the library over for the account they just logged in with.
                var ownerWarning = syncService.TakeSongLibraryOwnerWarning();
                if (ownerWarning != null)
                {
                    bool takeOver = await new MessageBox(e => Console.WriteLine(e), window, this)
                        .AskYesNoAsync("Song library belongs to another account",
                            ownerWarning + "\n\nDo you want to take the library over for your account and sync anyway?");
                    if (!takeOver)
                    {
                        new MessageBox(e => Console.WriteLine(e), window, this)
                            .Show("Song library", "Nothing was synced. Log in with the account that owns this library or choose a different song library to sync with this account.");
                        return;
                    }

                    // User agreed: sync the data and register the library for the current account.
                    await Task.Run(() => syncService.Pull(AdoptSongLibraryOnMismatch: true));
                }

                // Pulling rewrites the local database and may rename files in the song library (song library
                // migrations), so refresh the in-memory song lists if a library is already configured. The
                // pull already reported "Pull succeeded!" by now, so tell the user that the remaining spinner
                // time is the (potentially slow) library scan.
                if (Config.Data.SongLibraryPath != null)
                {
                    syncStateLabel.Text = "Pull succeeded — refreshing song library…";
                    try
                    {
                        await Task.Run(() => songPlaybackService.UpdateAvailableSongPaths(Config.Data.SongLibraryPath));
                        syncStateLabel.Text = syncService.State;
                    }
                    catch (Exception ex)
                    {
                        syncStateLabel.Text = "Pull succeeded, but the song library refresh failed.";
                        new MessageBox(e => Console.WriteLine(e), window, this)
                            .Show("Song library refresh failed.", $"{ex}");
                    }
                }

                // A sync session exists now: songs that were registered and tagged before the login (or
                // whose upload was skipped because there was no session) are uploaded in the background.
                // The worker is a no-op when there is nothing pending, and the scan above already kicked
                // it if one ran.
                syncService.ProcessPendingSongUploadsInBackground();
            }
            catch (Exception ex)
            {
                new MessageBox(e => Console.WriteLine(e), window, this)
                    .Show("Can't pull.", $"{syncService.State}\n\n{ex}");
            }
        }
        finally
        {
            SetLoginBusy(false);
        }
    }

    private void RegisterButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var textBoxHost = this.GetNestedControl<TextBox>("hostTextBox");

            var url = syncService.GetAccountRegistrationAddress(textBoxHost.Text);
            window!.OpenUrlOnCurrentOsBrowser(url);
        }
        catch (Exception ex)
        {
            new MessageBox(e => Console.WriteLine(e), window, this)
                .Show("Can't open registration.", $"{syncService.State}\n\n{ex}");
        }
    }
}