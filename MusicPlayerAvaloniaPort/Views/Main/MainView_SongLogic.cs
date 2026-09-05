using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;

namespace MusicPlayerAvaloniaPort.Views.Main;

public partial class MainView : UserControl
{
    const bool FolderPickerFallbackEnabled = false;

    /// <summary>
    /// Resolves the song library folder: the configured path is used when present, otherwise the
    /// MUSIC_FOLDER environment variable or a folder picker dialog. Only resolves the folder, the
    /// actual scan happens later (see <see cref="ScanSongLibrary"/>).
    /// </summary>
    void ResolveSongLibraryPath()
    {
        if (Config.Data.SongLibraryPath == null)
        {
            string? folder = null;
            var envVar = Environment.GetEnvironmentVariable("MUSIC_FOLDER");
            if (!string.IsNullOrWhiteSpace(envVar))
            {
                Console.WriteLine("For music folder, using env var");
                folder = envVar;
            }
            else
            {
                Console.WriteLine("For music folder, showing OpenFolderPicker");
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var storageProvider = TopLevel.GetTopLevel(Window)!.StorageProvider;
                    var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions // If this isnt awaited it straight up doesnt work at all on linux
                    {
                        Title = "Select your Music Root Folder",
                        AllowMultiple = false,
                    });
                    var storageFolder = folders![0];
                    folder = storageFolder!.Path.AbsolutePath;
                }).Wait();
            }

            if (folder == null || !HelperFuncs.DirOrSubDirsContainMp3(folder))
                Window?.Close();

            // Set SongLibraryPath
            Config.Data.SongLibraryPath = folder;
        }
    }

    /// <summary>
    /// Startup sync like in the DXMG client (see Assets.cs): once the song library folder is known but
    /// before the library scan, try to pull the latest data from the sync server. That way the pulled
    /// upvotedSong rows and the applied song library migrations (file renames, see Pull()) line up with
    /// the files the scan afterwards is going to find. Fails silently when the user is not logged in yet
    /// or the server is unreachable; logging in via the options view pulls again.
    /// </summary>
    void StartupSync()
    {
        if (Config.Data.SongLibraryPath == null)
            return; // No song library folder resolved (yet), nothing to check or apply

        var syncService = ServiceContainer.GetService<SongSyncService>();
        syncService.Pull();

        // If the song library is registered for a different account, the pull was aborted BEFORE
        // anything was synced (local database and library state file are untouched). This runs on the
        // background song setup thread, so ask the user on the UI thread and block until they answer:
        // only with their consent is the library taken over and the pull retried.
        var ownerWarning = syncService.TakeSongLibraryOwnerWarning();
        if (ownerWarning != null)
        {
            string warning = ownerWarning;
            bool? takeOver = null;
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                takeOver = await new MessageBox(ex => Console.WriteLine(ex), Window, this)
                    .AskYesNoAsync("Song library belongs to another account",
                        warning + "\n\nDo you want to take the library over for your account and sync anyway?");
            }).Wait();

            if (takeOver == true)
                syncService.Pull(AdoptSongLibraryOnMismatch: true);
            else
                Console.WriteLine("Song library sync skipped: the library is registered for another account.");
        }
    }

    /// <summary>
    /// Scans the resolved song library folder and builds the in-memory song lists. Has to run after the
    /// startup sync, so the file names, the database rows and the applied song library migrations line up.
    /// </summary>
    void ScanSongLibrary()
    {
        if (Config.Data.SongLibraryPath != null)
            songPlaybackService.UpdateAvailableSongPaths(Config.Data.SongLibraryPath);
    }

    void UpdateUiForNewSong(AvailableSong song)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var songName = Path.GetFileNameWithoutExtension(song.FilePath);

            var titleControl = this.GetLogicalDescendants().OfType<CustomRenderControl_Title>().FirstOrDefault(x => x.Name == "CustomRenderControl_Title");
            titleControl!.UpdateTitleText(songName);
            titleControl.InvalidateVisual();
        });
    }

    void UpdateUiForNewUpvoteLockedInState(bool lockedIn)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ViewModel!.UpvoteLockedIn = lockedIn;
            UpdateButtonUpvoteColor();
        });
    }

    DateTime lastPointerWheelChangedEvent = DateTime.MinValue;
    private void MainView_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((DateTime.Now - lastPointerWheelChangedEvent).TotalSeconds > 3)
        {
            Debug.WriteLine($"Scrollwheel event! {e.Delta.Y}");
            lastPointerWheelChangedEvent = DateTime.Now;
            if (e.Delta.Y > 0)
            {
                songPlaybackService.GetNextSong();
            }
            else if (e.Delta.Y < 0)
            {
                songPlaybackService.GetPreviousSong();
            }
        }
    }

    void ButtonUpvote_Click(object? sender, RoutedEventArgs e)
    {
        // Logic update
        ViewModel?.UpvoteLockedIn = !ViewModel.UpvoteLockedIn;
        songPlaybackService.UpvoteLockedIn = ViewModel!.UpvoteLockedIn;

        UpdateButtonUpvoteColor();
    }

    void UpdateButtonUpvoteColor()
    {
        var upvoteButton = this.GetLogicalDescendants().OfType<Button>().FirstOrDefault(x => x.Name == "ButtonUpvote");
        var path = upvoteButton?.GetLogicalChildren().FirstOrDefault() as Avalonia.Controls.Shapes.Path;
        path?.Fill = ViewModel?.UpvoteLockedIn == true ? this.FindResource("PrimaryColor") as SolidColorBrush : Brushes.White;
    }

    private void DurationBarStackPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Debug.WriteLine("DurationBarStackPanel_PointerPressed!");
        DurationBarStackPanel_PointerDown(sender, e);
    }

    private void DurationBarStackPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        //Debug.WriteLine("DurationBarStackPanel_PointerMoved!");
        DurationBarStackPanel_PointerDown(sender, e);
    }

    void DurationBarStackPanel_PointerDown(object? sender, PointerEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed)
            return;
        if (sender is not Control eventRoot)
        {
            Debug.WriteLine("eventRoot null?");
            return;
        }

        var clickPoint = e.GetPosition(eventRoot);
        var targetPercentage = (clickPoint.X - 3) / (eventRoot.Bounds.Width - 7); // I love magic numbers
        audioLibWrapper.PlayProgress = (float)targetPercentage;

        e.Handled = true;
    }

    // ---------- Quick Play (the DxMGP console "Play Song:" flow) ----------

    /// <summary>
    /// True while a quick play dialog is open - guards against re-entering the flow while the K key is
    /// held down (key repeat) or pressed repeatedly.
    /// </summary>
    bool quickPlayDialogOpen;

    /// <summary>
    /// "Play a song quickly": the DxMGP client opened its console with K, where a typed song name was
    /// matched against the library with the modified Levenshtein distance, the best fitting song was
    /// played and the other well fitting songs were printed. This port has no console window, so the
    /// same flow runs through message boxes:
    /// 1. ask which song to play,
    /// 2. start playing the best fitting song,
    /// 3. show a message box with the top 5 found songs.
    /// </summary>
    async void QuickPlaySong()
    {
        if (quickPlayDialogOpen)
            return;
        quickPlayDialogOpen = true;
        try
        {
            if (songPlaybackService.AvailableSongsCount == 0)
                return;

            var messageBox = new MessageBox(ex => Console.WriteLine(ex), Window, this);
            string input = await messageBox.GetTextAsync("Which song do you want to play?");
            if (string.IsNullOrWhiteSpace(input))
                return; // Canceled (empty input / closed the dialog)

            var matches = songPlaybackService.FindBestSongMatches(input, maxResults: 5);
            if (matches.Count == 0)
                return;

            // Play the best fitting song, then show all top 5 matches (like the console printed the
            // chosen song plus the other well fitting ones).
            AvailableSong best = matches[0].Song;
            songPlaybackService.PlaySpecificSong(best);

            string message = string.Join("\n", matches.Select((match, index) =>
            {
                string name = Path.GetFileNameWithoutExtension(match.Song.FilePath);
                string suffix = index == 0 ? "  <- now playing" : "";
                return $"{index + 1}. \"{name}\" (difference {match.Difference:0.00}){suffix}";
            }));

            await messageBox.ShowAsync($"Now playing: {Path.GetFileNameWithoutExtension(best.FilePath)}",
                message, width: 640, height: 220);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
        finally
        {
            quickPlayDialogOpen = false;
        }
    }
}