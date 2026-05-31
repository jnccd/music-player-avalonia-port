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

    public UpvotedSong? FindUpvotedSong(string songPath, SongDbContext? songDbContext)
    {
        songDbContext ??= new SongDbContext();

        // TODO: The matching logic should ideally be part of the interface repo since it concerns all projects using the db schema
        var fileName = Path.GetFileName(songPath);

        var filenameMatchingSongs = songDbContext.UpvotedSongs.Where(x => x.Name == fileName).ToArray();
        if (filenameMatchingSongs.Length == 1)
            return filenameMatchingSongs.First();
        if (filenameMatchingSongs.Length == 0)
            return null; // No songs at all means nothing we can do

        (var album, var artists) = HelperFuncs.GetAlbumAndArtistsFromSong(songPath);
        var fullMatchingSongs = songDbContext.UpvotedSongs.Where(x => x.Name == fileName && x.Album == album && x.Artist == artists).ToArray();
        if (filenameMatchingSongs.Length == 1)
            return fullMatchingSongs.First();

        throw new Exception("Master Skywalker there are too many of them what are we going to do!?");
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