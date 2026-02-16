using Avalonia;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using Size = Avalonia.Size;

namespace MusicPlayerAvaloniaPort.Configuration
{
    public class ConfigData
    {
        // Local Gui Settings
        public PixelPoint? Pos;
        public double? Width;
        public double? Height;
        public Color PrimaryColor;
        public string? SongLibraryPath;

        public ConfigData()
        {
            _ = Color.TryParse("#007B82", out PrimaryColor);
        }
    }
}
