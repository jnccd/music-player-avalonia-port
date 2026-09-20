using System;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongInfoService))]
public class SongInfoService()
{
    public string? GetCoverArtUrlOfSong(AvailableSong? availableSong)
    {
        var bytes = GetCoverArtBytesOfSong(availableSong);
        return bytes == null ? null : $"data:image/*;base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>
    /// The raw bytes of the first embedded cover art picture of the song, or null when the file has none
    /// (or cannot be read). UI frameworks that draw the image themselves - the mobile client decodes it
    /// into an Avalonia bitmap - use this instead of the data-url form of
    /// <see cref="GetCoverArtUrlOfSong"/>.
    /// </summary>
    public byte[]? GetCoverArtBytesOfSong(AvailableSong? availableSong)
    {
        try
        {
            using var file = TagLib.File.Create(availableSong?.FilePath);
            if (file.Tag.Pictures.Length > 0)
                return file.Tag.Pictures[0].Data.Data;
        }
        catch { }

        return null;
    }
}
