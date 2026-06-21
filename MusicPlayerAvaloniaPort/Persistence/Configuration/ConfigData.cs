using Avalonia;
using Avalonia.Media;

namespace MusicPlayerAvaloniaPort.Persistence.Configuration;

public class ConfigData
{
    // Local Gui Settings
    public PixelPoint? Pos { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public Color PrimaryColor { get; set; }

    public string? SongLibraryPath { get; set; }
    public float Volume { get; set; } = 0.8f;

    // Sync settings
    public string? AuthBackendRefreshToken { get; set; }
    public string? SyncServerHost { get; set; }
    public string? SyncServerUsername { get; set; }

    public ConfigData()
    {
        PrimaryColor = Color.Parse("#007B82");
    }
}
