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

            // Record the album/artist tags of the file when they can be read. A song is identified by its
            // file name plus its tags, so registering a file without its tags can create a duplicate entry
            // of a song another client already registered WITH its tags (the two rows would differ in tag
            // completeness and could not be proven to be the same song anymore).
            try
            {
                var (album, artists) = HelperFuncs.GetAlbumAndArtistsFromSong(songPath);
                newUpvotedSong.Album = album ?? "";
                newUpvotedSong.Artist = artists ?? "";
            }
            catch
            {
                // The tags could not be read (e.g. a file that is just being downloaded): keep the entry
                // metadata-less, it can then only be identified by its file name.
            }

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

            // The server data can contain duplicate entries of one song (e.g. two clients of the same
            // account registered the same file separately, each under its own SongId, before the server
            // started rejecting duplicate uploads). Merge them right after the rewrite, so the statistics
            // and the song matching only ever see one entry per song.
            MergeDuplicateUpvotedSongs(Config.Data.SongLibraryPath);
        }

        /// <summary>
        /// Merges duplicate entries of the same song in the local database (see
        /// MusicPlayerSyncInterface.SongFileMatching for the canonical rules). Two kinds of duplicates:
        /// 1. Exact duplicates (same file name AND same stored album/artist tags) - always merged.
        /// 2. Duplicates that differ in tag completeness: metadata-less entries ("" tags) plus entries
        ///    that carry the album/artist of the song. Those are absorbed into the tagged entry when all
        ///    tagged entries of that file name share ONE tag signature. When songLibraryPath is given,
        ///    the merge is only done if the files of the library agree (every file of that name carries
        ///    these tags); without a library the single-signature rule alone decides, like on the server.
        /// The merged-away entries are deleted together with their history rows (the canonical entry keeps
        /// its own). Returns the number of merged-away entries.
        /// </summary>
        public int MergeDuplicateUpvotedSongs(string? songLibraryPath = null)
        {
            int mergedAway = 0;

            // 1. Exact duplicates: same user, file name and stored album/artist tags.
            var duplicateGroups = SongDbContext.UpvotedSongs
                .ToArray()
                .GroupBy(s => new { s.UserId, s.Name, s.Artist, s.Album })
                .Where(group => group.Count() > 1)
                .ToArray();
            foreach (var group in duplicateGroups)
            {
                var (keep, remove) = SongFileMatching.MergeSameSongEntries(group);
                mergedAway += RemoveUpvotedSongRows(remove);
                Console.WriteLine($"Merged {remove.Length} exact duplicate(s) of \"{keep.Name}\" into {keep.SongId}.");
            }

            if (mergedAway > 0)
                SongDbContext.SaveChanges(); // Persist pass 1, so pass 2 only sees the surviving rows

            // 2. Tag-completeness duplicates (metadata-less entries absorbed into the tagged entry).
            bool libraryAvailable = !string.IsNullOrWhiteSpace(songLibraryPath) && Directory.Exists(songLibraryPath);
            var tagCompletenessGroups = SongDbContext.UpvotedSongs
                .ToArray() // re-read after the exact duplicates were removed above
                .GroupBy(s => new { s.UserId, s.Name })
                .Where(group => group.Count() > 1)
                .Where(group => group.Any(s => SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album))
                             && group.Any(s => !SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album)))
                .ToArray();
            foreach (var group in tagCompletenessGroups)
            {
                var taggedRows = group.Where(s => !SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album)).ToArray();
                var tagSignatures = taggedRows.Select(s => (s.Artist, s.Album)).Distinct().ToArray();
                if (tagSignatures.Length != 1)
                    continue; // Several differently tagged songs share the file name: a metadata-less row is ambiguous
                (string fileArtist, string fileAlbum) = tagSignatures[0];

                // When a song library is available, only merge when its files agree: every file of that
                // name in the library must carry these tags, otherwise the metadata-less rows could belong
                // to a different same-named file. Without a library the single-signature rule decides.
                if (libraryAvailable)
                {
                    var files = SongSyncService.FindSongFilesByName(songLibraryPath!, group.Key.Name);
                    if (files.Count > 0 && files.Any(file => !SongSyncService.SongFileMatchesTags(file, fileArtist, fileAlbum)))
                        continue;
                }

                var (keep, remove) = SongFileMatching.MergeSameSongEntries(group, fileAlbum, fileArtist);
                mergedAway += RemoveUpvotedSongRows(remove);
                Console.WriteLine($"Merged {remove.Length} metadata-less duplicate(s) of \"{keep.Name}\" into {keep.SongId}.");
            }

            if (mergedAway > 0)
                SongDbContext.SaveChanges();

            return mergedAway;
        }

        int RemoveUpvotedSongRows(UpvotedSong[] remove)
        {
            if (remove.Length == 0)
                return 0;

            // Drop the history entries of the merged-away rows with them (the kept row keeps its own).
            // Queried per row: EF Core 8 on .NET 10 cannot parameterize "array.Contains(...)" in a query
            // (it tries to compile a ReadOnlySpan closure and throws), so the ids are compared one by one.
            foreach (UpvotedSong removed in remove)
            {
                var orphanedHistory = SongDbContext.SongHistoryEntries
                    .Where(h => h.SongId == removed.SongId)
                    .ToArray();
                if (orphanedHistory.Length > 0)
                    SongDbContext.SongHistoryEntries.RemoveRange(orphanedHistory);
            }

            SongDbContext.UpvotedSongs.RemoveRange(remove);
            return remove.Length;
        }

        /// <summary>
        /// Rewrites every queued (not yet synced) request that refers to fromSongId so it refers to
        /// toSongId instead (the SongId in the serialized body and the BelongedToSongId marker). Used when
        /// the server rejects a queued "/sync/new-song" upload as a duplicate and returns the existing row
        /// of the song: the upload itself is dropped by the caller, while queued votes/volume changes that
        /// used the client-local SongId of the song are redirected to the server row, so they are not lost
        /// and do not get stuck forever. Returns how many queued entries were redirected.
        /// </summary>
        public int RedirectQueuedEntriesToSong(Guid fromSongId, Guid toSongId)
        {
            if (fromSongId == Guid.Empty || fromSongId == toSongId)
                return 0;

            // Guid.ToString() (lowercase "d") is the format System.Text.Json writes, so a plain replace is
            // safe: the SongId is the only Guid in these request bodies.
            string fromIdString = fromSongId.ToString();
            var affected = SongDbContext.NotYetSyncedData
                .Where(x => x.BelongedToSongId == fromSongId && x.Body.Contains(fromIdString))
                .ToArray();

            foreach (var queued in affected)
            {
                queued.Body = queued.Body.Replace(fromIdString, toSongId.ToString());
                queued.BelongedToSongId = toSongId;
            }

            if (affected.Length > 0)
                SongDbContext.SaveChanges();

            return affected.Length;
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
            // Materialize first: EF Core 8 on .NET 10 cannot parameterize "array.Contains(...)" in a query
            // (it tries to compile a ReadOnlySpan closure and throws), so the filter runs in memory.
            var queuedUploadsToRename = SongDbContext.NotYetSyncedData
                .Where(x => x.Endpoint.EndsWith("/sync/new-song") && x.BelongedToSongId != null)
                .ToArray()
                .Where(x => x.BelongedToSongId != null && songIds.Contains(x.BelongedToSongId.Value))
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