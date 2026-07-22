using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Song;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class OptionsViewModel : ViewModelBase
{
    SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();

    // --- Properties ---

    // Sync
    private string? _syncServerHost;
    public string? SyncServerHost
    {
        get { return Config.Data.SyncServerHost; }
        set { Config.Data.SyncServerHost = value; SetProperty(ref _syncServerHost, value); }
    }
    private string? _syncServerUsername;
    public string? SyncServerUsername
    {
        get { return Config.Data.SyncServerUsername; }
        set { Config.Data.SyncServerUsername = value; SetProperty(ref _syncServerUsername, value); }
    }

    private string? _downloadFolderPath;
    public string? DownloadFolderPath
    {
        get { return Config.Data.DownloadFolderPath; }
        set { SetProperty(ref _downloadFolderPath, value); }
    }

    private string? _musicLibraryFolderPath;
    public string? MusicLibraryFolderPath
    {
        get { return Config.Data.SongLibraryPath; }
        set { SetProperty(ref _musicLibraryFolderPath, value); }
    }

    public string? MusicLibrarySongCount
    {
        get { return songPlaybackService.AvailableSongsCount.ToString(); }
    }

    // --- Commands ---

    // ...
}
