using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerSyncInterface;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncInterface.DTOs.Composites;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(DbWrapperService))]
public class DbWrapperService
{
    public Context GetContext() => new(this);

    public class Context(DbWrapperService parent) : IDisposable
    {
        SongDbContext SongDbContext { get; } = new SongDbContext();

        public void SaveChanges()
        {
            SongDbContext.SaveChanges();
        }

        // Create
        public UpvotedSong AddNewUpvotedSong([StringSyntax(StringSyntaxAttribute.Uri)] string songPath)
        {
            // Add empty local user if necessary
            if (!SongDbContext.Users.Any(x => x.UserId == ""))
            {
                SongDbContext.Users.Add(new User("", "", ""));
                SongDbContext.SaveChanges();
            }

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
            SongDbContext.UpvotedSongs.FirstOrDefault(x => x.SongId == Id && (x.UserId == "" || x.UserId == Config.Data.SyncServerUsername))
                ?? throw new InvalidDataException($"SongId {Id} not found!");
        public UpvotedSong? GetUpvotedSongByFullPath([StringSyntax(StringSyntaxAttribute.Uri)] string fullSongPath)
        {
            // The matching logic lives in the interface project (see MusicPlayerSyncInterface.SongFileMatching),
            // since it concerns all projects that use the db schema and the song library.
            var fileName = Path.GetFileName(fullSongPath);
            var candidateSongs = SongDbContext.UpvotedSongs
                .Where(x => x.UserId == "" || x.UserId == Config.Data.SyncServerUsername)
                .ToArray();

            return SongFileMatching.ResolveUpvotedSongEntry(fileName, candidateSongs, () =>
            {
                var (album, artists) = HelperFuncs.GetAlbumAndArtistsFromSong(fullSongPath);
                return (album, artists);
            });
        }
        public bool DoesSongHaveVolume(Guid? SongId) =>
            SongDbContext.UpvotedSongs.FirstOrDefault(x => x.SongId == SongId && (x.UserId == "" || x.UserId == Config.Data.SyncServerUsername))?.Volume > 0;
        public UpvotedSong[] DumpUpvotedSongs() =>
            [.. SongDbContext.UpvotedSongs.Where(x => x.UserId == "" || x.UserId == Config.Data.SyncServerUsername)];

        // Sync
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
        public IEnumerable<NotYetSyncedData> GetNotYetSyncedDataEntries()
        {
            return SongDbContext.NotYetSyncedData.ToArray();
        }
        public void RemoveNotYetSyncedDataEntries(NotYetSyncedData unsyncedData)
        {
            SongDbContext.NotYetSyncedData.Remove(unsyncedData);
            SongDbContext.SaveChanges();
        }

        /// <summary>
        /// Renames the song inside queued "/sync/new-song" upload bodies of the given songs. The queued
        /// bodies were serialized when the song was added, so after a song got renamed they would otherwise
        /// still create the server row under the old name when they are retried.
        /// </summary>
        public void RenameQueuedSongUploads(Guid[] songIds, string newName)
        {
            var queuedUploadsToRename = SongDbContext.NotYetSyncedData
                .Where(x => x.Endpoint.EndsWith("/sync/new-song") && x.BelongedToSongId != null && songIds.Contains(x.BelongedToSongId.Value))
                .ToArray();
            foreach (var queuedUpload in queuedUploadsToRename)
            {
                var queuedSong = JsonSerializer.Deserialize<UpvotedSong>(queuedUpload.Body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (queuedSong == null)
                    continue;
                queuedSong.Name = newName;
                queuedUpload.Body = JsonSerializer.Serialize(queuedSong, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
            SongDbContext.SaveChanges();
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