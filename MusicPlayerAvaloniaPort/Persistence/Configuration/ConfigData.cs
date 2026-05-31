using Avalonia;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using Size = Avalonia.Size;

namespace MusicPlayerAvaloniaPort.Persistence.Configuration;

public class ConfigData
{
    // Local Gui Settings
    public PixelPoint? Pos;
    public double? Width;
    public double? Height;
    public Color PrimaryColor;

    public string? SongLibraryPath;
    public double Volume = 0.8;

    // Sync settings
    public string? AuthBackendRefreshToken;
    public string? SyncServerHost;
    public string? SyncServerUsername;

    public ConfigData()
    {
        _ = Color.TryParse("#007B82", out PrimaryColor);
    }
}
