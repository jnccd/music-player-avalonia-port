using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayerAvaloniaPort.Configuration;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services;
using MusicPlayerAvaloniaPort.Services.Song;
using static MusicPlayerAvaloniaPort.Views.MainView.UiLoopTitle;

namespace MusicPlayerAvaloniaPort.Views.MainView;

public partial class MainView : UserControl
{
    void MapLocalSongLibrary()
    {
        if (Config.Data.SongLibraryPath == null)
        {
            // Get the StorageProvider from your window
            var window = this.GetVisualRoot() as Window;
            var storageProvider = TopLevel.GetTopLevel(window)!.StorageProvider;

            // Use folder dialog
            var folders = storageProvider?.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select your Music Root Folder",
                AllowMultiple = false
            }).Result;
            var folder = folders?.FirstOrDefault();

            if (folder == null || !HelperFuncs.DirOrSubDirsContainMp3(folder.Path.AbsolutePath))
                window?.Close();

            // Set SongLibraryPath
            Config.Data.SongLibraryPath = folder!.Path.AbsolutePath;
        }

        songManager.UpdateAvailableSongPaths(Config.Data.SongLibraryPath);
    }

    void UpdateUiForNewSong(AvailableSong song)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var songName = Path.GetFileNameWithoutExtension(song.FilePath);
            uiUpdateLoop.InvokeEvent(new UpdateTitleEventArgs(songName));
        });
    }

    private void MainView_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
        {
            songManager.GetNextSong();
        }
        else if (e.Delta.Y < 0)
        {
            songManager.GetPreviousSong();
        }
    }

    void ButtonUpvote_Click(object? sender, RoutedEventArgs e)
    {
        viewModel?.UpvoteLocked = !viewModel.UpvoteLocked;
        var upvoteButton = sender as Button;

        var path = upvoteButton?.GetLogicalChildren().FirstOrDefault() as Avalonia.Controls.Shapes.Path;
        path?.Fill = viewModel?.UpvoteLocked == true ? this.FindResource("PrimaryColor") as SolidColorBrush : Brushes.White;
    }

    private void DurationBarStackPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Debug.WriteLine("DurationBarStackPanel_PointerPressed!");
        DurationBarStackPanel_PointerDown(sender, e);
    }

    private void DurationBarStackPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        Debug.WriteLine("DurationBarStackPanel_PointerMoved!");
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