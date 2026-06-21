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
using MusicPlayerAvaloniaPort.Services.Song;
using static MusicPlayerAvaloniaPort.Views.Main.UiLoopTitle;

namespace MusicPlayerAvaloniaPort.Views.Main;

public partial class MainView : UserControl
{
    const bool FolderPickerFallbackEnabled = false;

    void MapLocalSongLibrary()
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
            else if (FolderPickerFallbackEnabled)
            {
#pragma warning disable CS0162 // Unreachable code detected
                Console.WriteLine("For music folder, showing MessageBox");
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var mb = new MessageBox(e => Console.WriteLine(e), window, null);
                    folder = mb.GetText("Input the song library path, FolderPicker doesnt work on Linux");
                }).Wait();
#pragma warning restore CS0162 // Unreachable code detected
            }
            else
            {
                Console.WriteLine("For music folder, showing OpenFolderPicker");
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var storageProvider = TopLevel.GetTopLevel(window)!.StorageProvider;
                    var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions // If this isnt awaited it straight up doesnt work at all on linux
                    {
                        Title = "Select your Music Root Folder",
                        AllowMultiple = false
                    });
                    var storageFolder = folders![0];
                    folder = storageFolder!.Path.AbsolutePath;
                }).Wait();
            }

            if (folder == null || !HelperFuncs.DirOrSubDirsContainMp3(folder))
                window?.Close();

            // Set SongLibraryPath
            Config.Data.SongLibraryPath = folder;
        }

        songPlaybackService.UpdateAvailableSongPaths(Config.Data.SongLibraryPath!);
    }

    void UpdateUiForNewSong(AvailableSong song)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var songName = Path.GetFileNameWithoutExtension(song.FilePath);
            uiUpdateLoop.InvokeEvent(new UpdateTitleEventArgs(songName));
        });
    }

    void UpdateUiForNewUpvoteLockedInState(bool lockedIn)
    {
        Dispatcher.UIThread.Post(() =>
        {
            viewModel!.UpvoteLockedIn = lockedIn;
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
        viewModel?.UpvoteLockedIn = !viewModel.UpvoteLockedIn;
        songPlaybackService.UpvoteLockedIn = viewModel!.UpvoteLockedIn;

        UpdateButtonUpvoteColor();
    }

    void UpdateButtonUpvoteColor()
    {
        var upvoteButton = this.GetLogicalDescendants().OfType<Button>().FirstOrDefault(x => x.Name == "ButtonUpvote");
        var path = upvoteButton?.GetLogicalChildren().FirstOrDefault() as Avalonia.Controls.Shapes.Path;
        path?.Fill = viewModel?.UpvoteLockedIn == true ? this.FindResource("PrimaryColor") as SolidColorBrush : Brushes.White;
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
        if (sender is not StackPanel eventRoot)
        {
            Debug.WriteLine("eventRoot null?");
            return;
        }

        var clickPoint = e.GetPosition(eventRoot);
        var targetPercentage = (clickPoint.X - 3) / (eventRoot.Bounds.Width - 7); // I love magic numbers
        audioLibWrapper.PlayProgress = (float)targetPercentage;

        e.Handled = true;
    }
}