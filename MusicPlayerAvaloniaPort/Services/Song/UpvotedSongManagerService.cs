using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(UpvotedSongManagerService))]
public class UpvotedSongManagerService(UpvotedSongSyncService SyncService)
{
    public UpvotedSong RegisterNewUpvotedSong(string songPath)
    {
        var songFileName = Path.GetFileName(songPath);
        var newUpvotedSong = new UpvotedSong(songFileName, 0, 0, 0, 0, GetSongAgeFromPath(songPath), -1) { Path = songPath };

        using var songDbContext = new SongDbContext();
        songDbContext.UpvotedSongs.Add(newUpvotedSong);
        songDbContext.SaveChanges();

        SyncService.UploadNewSong(newUpvotedSong);

        return newUpvotedSong;
    }

    public static DateTimeOffset? GetSongAgeFromPath(string SongPath)
    {
        if (File.Exists(SongPath))
        {
            DateTimeOffset[] dates = [File.GetCreationTime(SongPath), File.GetLastWriteTime(SongPath)];
            return dates.Min();
        }
        else
            return null;
    }
}