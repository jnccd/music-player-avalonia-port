using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayerAvaloniaPort.Configuration;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services;
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

    void UpdateUiForNewSong(string CurrentSongPath)
    {
        var songName = Path.GetFileNameWithoutExtension(CurrentSongPath);
        uiUpdateLoop.InvokeEvent(new UpdateTitleEventArgs(songName));
    }
}