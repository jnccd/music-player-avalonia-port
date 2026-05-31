using MusicPlayerAvaloniaPort.Persistence.Configuration;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class OptionsViewModel : ViewModelBase
{
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

    // --- Commands ---

    // ...
}
