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
    /// <summary>
    /// Error text stored on queued "/sync/new-song" entries that were created by the lazy registration
    /// flow (library scan): the tags of the song have not been read and uploaded yet. Such entries are
    /// owned by the background tag/upload worker (SongSyncService.ProcessPendingSongUploadsInBackground)
    /// and are skipped by the plain startup retry loop.
    /// </summary>
    public const string PendingTagReadError = "Lazy registration: waiting for tag read + upload.";

    public Context GetContext() => new(this);

    public class Context(DbWrapperService parent) : IDisposable
    {
        SongDbContext SongDbContext { get; } = new SongDbContext();

        public void SaveChanges()
        {
            SongDbContext.SaveChanges();
        }

        // Create
        public UpvotedSong AddNewUpvotedSong([StringSyntax(StringSyntaxAttribute.Uri)] string songPath, (string Album, string Artists)? preReadTags = null)
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
            // completeness and could not be proven to be the same song anymore). Callers of a parallel
            // scan pass pre-read tags (reading them before the registration lock keeps the lock short);
            // without them the tags are read here.
            if (preReadTags.HasValue)
            {
                newUpvotedSong.Album = preReadTags.Value.Album;
                newUpvotedSong.Artist = preReadTags.Value.Artists;
            }
            else
            {
                var tags = parent.ReadTagsFromSongFile(songPath);
                newUpvotedSong.Album = tags.Album;
                newUpvotedSong.Artist = tags.Artists;
            }

            SongDbContext.UpvotedSongs.Add(newUpvotedSong);
            SongDbContext.SaveChanges();

            return newUpvotedSong;
        }

        /// <summary>
        /// Registers a brand new song row WITHOUT reading its tags from the file (the tag read on slow
        /// media such as a NAS is deferred to the background tag/upload worker, see
        /// SongSyncService.ProcessPendingSongUploadsInBackground) and WITHOUT uploading it yet. The row
        /// and its pending-upload marker are written in ONE SaveChanges, so a crash cannot leave a row
        /// without a marker (it would just be registered again on the next scan).
        /// </summary>
        public UpvotedSong AddNewUpvotedSongLazy([StringSyntax(StringSyntaxAttribute.Uri)] string songPath)
        {
            // Add empty local user if necessary (only the very first song has to do this).
            if (!SongDbContext.Users.Any(x => x.UserId == ""))
            {
                SongDbContext.Users.Add(new User("", "", ""));
            }

            var songFileName = Path.GetFileName(songPath);
            var newUpvotedSong = new UpvotedSong(songFileName, 0, 0, 0, 0, parent.GetSongAgeFromPath(songPath), -1) { Path = songPath };

            // Pending-upload marker: the tag/upload worker finds this entry, reads the tags of the file,
            // uploads the song WITH its tags and removes the marker. Until then the song stays tag-less
            // locally, which is also what makes the whole flow restart-safe (a killed worker leaves the
            // marker behind and the next scan picks it up again).
            var pendingUpload = new NotYetSyncedData(Guid.NewGuid(), "/sync/new-song",
                JsonSerializer.Serialize(newUpvotedSong, new JsonSerializerOptions { WriteIndented = true }),
                DbWrapperService.PendingTagReadError, newUpvotedSong.SongId);

            SongDbContext.UpvotedSongs.Add(newUpvotedSong);
            SongDbContext.NotYetSyncedData.Add(pendingUpload);
            SongDbContext.SaveChanges(); // One transaction: row + marker are either both there or neither

            return newUpvotedSong;
        }
        public NotYetSyncedData[] GetPendingSongUploads() =>
            SongDbContext.NotYetSyncedData
                .Where(x => x.Endpoint == "/sync/new-song" && x.BelongedToSongId != null)
                .ToArray();
        public UpvotedSong? GetUpvotedSongByIdOrNull(Guid? Id) =>
            SongDbContext.UpvotedSongs.FirstOrDefault(x => x.SongId == Id && (x.UserId == "" || x.UserId == Config.Data.SyncServerUsername));
        public UpvotedSong[] GetUpvotedSongsByName(string fileName) =>
            SongDbContext.UpvotedSongs
                .Where(x => (x.UserId == "" || x.UserId == Config.Data.SyncServerUsername) && x.Name == fileName)
                .ToArray();
        public UpvotedSong? GetUpvotedSongByTags(string fileName, string artist, string album) =>
            SongDbContext.UpvotedSongs.FirstOrDefault(x =>
                (x.UserId == "" || x.UserId == Config.Data.SyncServerUsername) && x.Name == fileName && x.Artist == artist && x.Album == album);

        /// <summary>
        /// Persists the given album/artist tags onto the row. If another row of the same user already
        /// carries exactly these tags for the same file name, the row is a duplicate that cannot take
        /// them (the unique index forbids it): the tag-less duplicate is removed (with its history) and
        /// false is returned. The caller should then drop the pending-upload marker instead of uploading.
        /// </summary>
        public bool TryApplyTagsToSong(Guid songId, string album, string artist, out bool removedAsDuplicate)
        {
            removedAsDuplicate = false;
            var row = GetUpvotedSongByIdOrNull(songId);
            if (row == null)
                return false; // Row vanished in the meantime (e.g. server merge + pull)

            var identityDuplicate = SongDbContext.UpvotedSongs.FirstOrDefault(x =>
                x.UserId == row.UserId && x.Name == row.Name && x.Artist == artist && x.Album == album && x.SongId != row.SongId);
            if (identityDuplicate != null)
            {
                // The tags belong to another (already tagged) row - this row was a metadata-less
                // duplicate of it, so it is removed instead of updated.
                RemoveUpvotedSongRows([row]);
                removedAsDuplicate = true;
                return false;
            }

            row.Artist = artist;
            row.Album = album;
            SongDbContext.SaveChanges();
            return true;
        }
        public void UpdateNotYetSyncedDataEntry(NotYetSyncedData entry, string? newBody, string? newError)
        {
            if (newBody != null)
                entry.Body = newBody;
            if (newError != null)
                entry.Error = newError;
            SongDbContext.SaveChanges();
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
            // This is a hot path (it runs once per song file during library scans), so filter on the
            // database side instead of loading every row of the user and filtering in memory.
            var fileName = Path.GetFileName(fullSongPath);
            var sameNameSongs = SongDbContext.UpvotedSongs
                .Where(x => (x.UserId == "" || x.UserId == Config.Data.SyncServerUsername) && x.Name == fileName)
                .ToArray();

            // Zero or one entry with the file name: the file name alone identifies the song, so the tags
            // of the file are never read here - reading them (file IO) is only needed to disambiguate
            // when several entries share the file name.
            if (sameNameSongs.Length <= 1)
                return sameNameSongs.FirstOrDefault();

            return SongFileMatching.ResolveUpvotedSongEntry(fileName, sameNameSongs, () =>
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
        /// <summary>
        /// Replaces the whole local database with the pulled data, then merges duplicate entries of the
        /// same song (exact and tag-completeness duplicates, see <see cref="MergeDuplicateUpvotedSongs"/>).
        /// Returns how many duplicate rows were merged away.
        /// </summary>
        public int RewriteDatabase(SyncPullResponse pulledData)
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
            return MergeDuplicateUpvotedSongs(Config.Data.SongLibraryPath);
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

            // The library is enumerated ONCE (walking a NAS recursively for every duplicate group would
            // be far too slow); the resulting name->files map is then used for all groups.
            Dictionary<string, List<string>>? libraryFilesByName = null;
            if (libraryAvailable && tagCompletenessGroups.Length > 0)
            {
                libraryFilesByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string filePath in HelperFuncs.FindAllMp3FilesInDir(songLibraryPath!))
                {
                    string name = Path.GetFileName(filePath);
                    if (libraryFilesByName.TryGetValue(name, out var list))
                        list.Add(filePath);
                    else
                        libraryFilesByName[name] = new List<string> { filePath };
                }
            }

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
                if (libraryAvailable
                    && libraryFilesByName!.TryGetValue(group.Key.Name, out var files)
                    && files.Count > 0
                    && files.Any(file => !SongSyncService.SongFileMatchesTags(file, fileArtist, fileAlbum)))
                    continue;

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
        /// Removes one upvoted song entry together with its history entries and its queued (not yet
        /// synced) requests (votes/volume changes that can never succeed meaningfully anymore once the
        /// entry is gone). The caller is expected to have created the server-side deletion migration
        /// and to have deleted the song files already (mirrors the delete flow of the DxMGP port).
        /// </summary>
        public void RemoveUpvotedSongEntry(Guid songId)
        {
            var historyToRemove = SongDbContext.SongHistoryEntries
                .Where(h => h.SongId == songId)
                .ToArray();
            var queuedToRemove = SongDbContext.NotYetSyncedData
                .Where(n => n.BelongedToSongId == songId)
                .ToArray();
            var rowsToRemove = SongDbContext.UpvotedSongs
                .Where(s => s.SongId == songId)
                .ToArray();

            if (historyToRemove.Length > 0)
                SongDbContext.SongHistoryEntries.RemoveRange(historyToRemove);
            if (queuedToRemove.Length > 0)
                SongDbContext.NotYetSyncedData.RemoveRange(queuedToRemove);
            if (rowsToRemove.Length > 0)
                SongDbContext.UpvotedSongs.RemoveRange(rowsToRemove);

            SongDbContext.SaveChanges();
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

    /// <summary>
    /// Reads the album/artist tags of a song file (same convention as everywhere in this codebase, see
    /// MusicPlayerSyncInterface.SongFileMatching). Returns empty strings when the tags cannot be read -
    /// the entry is then metadata-less and can only be identified by its file name.
    /// </summary>
    public (string Album, string Artists) ReadTagsFromSongFile([StringSyntax(StringSyntaxAttribute.Uri)] string songPath)
    {
        try
        {
            var (album, artists) = HelperFuncs.GetAlbumAndArtistsFromSong(songPath);
            return (album ?? "", artists ?? "");
        }
        catch
        {
            return ("", "");
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