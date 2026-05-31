using MusicPlayerAvaloniaPort.Persistence.Configuration;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class OptionsViewModel : ViewModelBase
{
    // --- Properties ---

    // Sync
    public string? SyncServerHost
    {
        get { return Config.Data.SyncServerHost; }
        set { Config.Data.SyncServerHost = value; SetProperty(ref Config.Data.SyncServerHost, value); }
    }
    public string? SyncServerUsername
    {
        get { return Config.Data.SyncServerUsername; }
        set { Config.Data.SyncServerUsername = value; SetProperty(ref Config.Data.SyncServerUsername, value); }
    }

    // --- Commands ---

    // ...
}
