using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MusicPlayerAvaloniaPort.Configuration;

namespace MusicPlayerAvaloniaPort;

public partial class MainView : UserControl
{
    SongManager songManager = new();
    AudioLibWrapper audioLibWrapper = new();

    void MapLocalSongLibrary()
    {
        if (Config.Data.SongLibraryPath == null)
        {
            // Get the StorageProvider from your window
            var window = this.GetVisualRoot() as Window;
            var storageProvider = TopLevel.GetTopLevel(window)!.StorageProvider;
            var folders = storageProvider?.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select your Music Root Folder",
                AllowMultiple = false
            }).Result;
            var folder = folders?.FirstOrDefault();

            if (folder == null || !Helpers.DirOrSubDirsContainMp3(folder.Path.AbsolutePath))
                window?.Close();

            Config.Data.SongLibraryPath = folder!.Path.AbsolutePath;
        }

        songManager.AvailableSongPaths = Helpers.FindAllMp3FilesInDir(Config.Data.SongLibraryPath);
    }

    void InitPlayingCurrentSong(string CurrentSongPath)
    {
        var songName = Path.GetFileNameWithoutExtension(CurrentSongPath);
        ChangeTitle(songName);
        audioLibWrapper.PlaySong(CurrentSongPath);
    }
}