using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongInfoService))]
public class SongInfoService()
{
    public string? GetCoverArtUrlOfSong(AvailableSong? availableSong)
    {
        try
        {
            using var file = TagLib.File.Create(availableSong?.FilePath);
            if (file.Tag.Pictures.Length > 0)
            {
                var picture = file.Tag.Pictures[0];
                string artUrl = $"data:{picture.MimeType};base64,{Convert.ToBase64String(picture.Data.Data)}";
                return artUrl;
            }
        }
        catch { }

        return null;
    }
}