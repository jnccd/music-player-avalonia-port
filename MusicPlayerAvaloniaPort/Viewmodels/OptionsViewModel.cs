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

    // General
    private bool _lowPowerMode;
    /// <summary>
    /// Reduces the CPU footprint of the visualizations (lower diagram frame rate, lower FFT analysis
    /// resolution) for devices on a limited power budget. Saved immediately so it survives even when
    /// the app is closed from the options window.
    /// </summary>
    public bool LowPowerMode
    {
        get { return Config.Data.LowPowerMode; }
        set
        {
            Config.Data.LowPowerMode = value;
            SetProperty(ref _lowPowerMode, value);
            Config.Save();
        }
    }

    // --- Commands ---

    // ...
}
