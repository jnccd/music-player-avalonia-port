using Avalonia;
using Avalonia.Media;

namespace MusicPlayerAvaloniaPort.Persistence.Configuration;

public class ConfigData
{
    // Local Gui Settings
    public int? WindowPositionX { get; set; }
    public int? WindowPositionY { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public Color PrimaryColor { get; set; }

    public string? SongLibraryPath { get; set; }
    public float Volume { get; set; } = 0.8f;

    public string? DownloadFolderPath { get; set; }

    // Sync settings
    public string? AuthBackendRefreshToken { get; set; }
    public string? SyncServerHost { get; set; }
    /// <summary>
    /// This is assumed to be equal to the <see cref="MusicPlayerSyncInterface.DTOs.User.UserId"/> / <see cref="MusicPlayerSyncInterface.DTOs.User.UserHandle"/> field in the User DTO class
    /// </summary>
    public string? SyncServerUsername { get; set; }

    public ConfigData()
    {
        PrimaryColor = Color.Parse("#007B82");
    }
}
