using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace MusicPlayerAvaloniaPort.Views.Options;

public partial class OptionsView : UserControl
{
    Window? window => TopLevel.GetTopLevel(this) as Window;

    readonly SongSyncService syncService = ServiceContainer.GetService<SongSyncService>();
    readonly SongDownloadRequestProcessorService songDownloadRequestProcessorService = ServiceContainer.GetService<SongDownloadRequestProcessorService>();
    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();

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