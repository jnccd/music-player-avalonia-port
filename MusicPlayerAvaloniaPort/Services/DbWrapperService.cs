using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncInterface.DTOs.Composites;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(DbWrapperService))]
public class DbWrapperService
{
    public Context GetContext() => new(this);

    public class Context(DbWrapperService parent) : IDisposable
    {
        SongDbContext SongDbContext { get; } = new SongDbContext();

        // Create
        public UpvotedSong AddNewUpvotedSong([StringSyntax(StringSyntaxAttribute.Uri)] string songPath)
        {
            var songFileName = Path.GetFileName(songPath);
            var newUpvotedSong = new UpvotedSong(songFileName, 0, 0, 0, 0, parent.GetSongAgeFromPath(songPath), -1) { Path = songPath };

            SongDbContext.UpvotedSongs.Add(newUpvotedSong);
            SongDbContext.SaveChanges();

            return newUpvotedSong;
        }
        public SongHistoryEntry AddNewSongHistoryEntry(Guid upvotedSongId, float scoreChange)
        {
            var newEntry = new SongHistoryEntry(upvotedSongId, scoreChange, DateTime.Now);
            SongDbContext.SongHistoryEntries.Add(newEntry);
            SongDbContext.SaveChanges();

            return newEntry;
        }
        public NotYetSyncedData AddNewNotYetSyncedDataEntry(string newEntryJson, string endpoint, string? error, Guid? SongId)
        {
            var newEntry = new NotYetSyncedData(Guid.NewGuid(), endpoint, newEntryJson, error, SongId);
            SongDbContext.NotYetSyncedData.Add(newEntry);
            SongDbContext.SaveChanges();

            return newEntry;
        }

        // Read
        public UpvotedSong GetUpvotedSongById(Guid? Id) =>
            SongDbContext.UpvotedSongs.FirstOrDefault(x => x.SongId == Id)
                ?? throw new InvalidDataException($"SongId {Id} not found!");
        public UpvotedSong? GetUpvotedSongByFullPath([StringSyntax(StringSyntaxAttribute.Uri)] string fullSongPath)
        {
            // TODO: The matching logic should ideally be part of the interface repo since it concerns all projects using the db schema
            var fileName = Path.GetFileName(fullSongPath);

            var filenameMatchingSongs = SongDbContext.UpvotedSongs.Where(x => x.Name == fileName).ToArray();
            if (filenameMatchingSongs.Length == 1)
                return filenameMatchingSongs.First();
            else if (filenameMatchingSongs.Length == 0)
                return null; // No songs at all means nothing we can do

            (var album, var artists) = HelperFuncs.GetAlbumAndArtistsFromSong(fullSongPath);
            var fullMatchingSongs = SongDbContext.UpvotedSongs.Where(x => x.Name == fileName && x.Album == album && x.Artist == artists).ToArray();
            if (fullMatchingSongs.Length == 1)
                return fullMatchingSongs.First();

            throw new Exception("Master Skywalker there are too many of them what are we going to do!?");
        }

        // Mixed
        public void RewriteDatabase(SyncPullResponse pulledData)
        {
            SongDbContext.SongHistoryEntries.RemoveRange(SongDbContext.SongHistoryEntries);
            SongDbContext.SaveChanges();
            SongDbContext.UpvotedSongs.RemoveRange(SongDbContext.UpvotedSongs);
            SongDbContext.SaveChanges();

            // Add missing user (should just be one, ourselves)
            User pulledUser = pulledData.User ?? throw new Exception($"pulledData contains no user!");
            if (!SongDbContext.Users.Where(x => x.UserId == pulledUser.UserId).Any())
                SongDbContext.Users.Add(pulledUser);
            SongDbContext.UpvotedSongs.AddRange(pulledData.Songs);
            SongDbContext.SaveChanges();
            SongDbContext.SongHistoryEntries.AddRange(pulledData.HistoryEntries);
            SongDbContext.SaveChanges();
        }
        public SyncInitRequest GetSyncInitRequest()
        {
            var songs = SongDbContext.UpvotedSongs.ToArray();
            var historyEntries = SongDbContext.SongHistoryEntries.ToArray();

            return new SyncInitRequest(songs, historyEntries);
        }

        public void Dispose()
        {
            SongDbContext.Dispose();
        }
    }

    DateTimeOffset? GetSongAgeFromPath([StringSyntax(StringSyntaxAttribute.Uri)] string SongPath)
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