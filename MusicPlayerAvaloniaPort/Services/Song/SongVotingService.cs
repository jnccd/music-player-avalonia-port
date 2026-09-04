using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongVotingService))]
public class SongVotingService(AudioLibWrapperService AudioLibWrapper, SongSyncService SyncService, SongChoosingService SongChoosingService, DbWrapperService DbWrapper)
{
    public event EventHandler<bool>? SongGotUpvoted;
    public event EventHandler<bool>? SongGotDownvoted;

    // Serializes "the song is not registered yet, so insert it": with a parallel library scan two files
    // of the very same song (e.g. copies in several folders) must not race past each other's lookup and
    // insert duplicate rows, and the database (SQLite) only has a single writer anyway.
    readonly object songRegistrationLock = new();

    public UpvotedSong RegisterNewUpvotedSong([StringSyntax(StringSyntaxAttribute.Uri)] string songPath, bool deferTagReadingAndUpload = false)
    {
        // deferTagReadingAndUpload = true is used by the library scan: on slow media (NAS) reading the
        // tags takes hundreds of ms per file, so the scan only inserts tag-less rows and leaves the tag
        // read + upload to the background worker (SongSyncService.ProcessPendingSongUploadsInBackground).
        // The row and a durable pending-upload marker are written in one transaction, so an app close at
        // any point can never lose the song or upload it without its tags.
        var tags = deferTagReadingAndUpload ? default : DbWrapper.ReadTagsFromSongFile(songPath);

        // The insert itself is serialized and the lookup is repeated inside the lock, so when two files
        // of the very same song race, the first registration wins and the second file maps to the same
        // row instead of inserting a duplicate. The lock only guards this quick check+insert.
        UpvotedSong newUpvotedSong;
        bool wasInserted = false;
        lock (songRegistrationLock)
        {
            using var dbContext = DbWrapper.GetContext();
            var existing = dbContext.GetUpvotedSongByFullPath(songPath);
            if (existing != null)
                newUpvotedSong = existing;
            else
            {
                newUpvotedSong = deferTagReadingAndUpload
                    ? dbContext.AddNewUpvotedSongLazy(songPath)
                    : dbContext.AddNewUpvotedSong(songPath, tags);
                wasInserted = true;
            }
        }

        // The upload happens OUTSIDE the lock: uploads of different songs are independent, so parallel
        // scans upload concurrently. When the song already exists on the server the upload is rejected
        // there and the queued data is redirected to the existing row (see /sync/new-song). Only the
        // registration that actually inserted the row uploads it - and only in the non-deferred mode
        // (deferred rows are uploaded by the background worker after their tags were read).
        if (wasInserted && !deferTagReadingAndUpload)
            SyncService.UploadNewSongEntry(newUpvotedSong);

        return newUpvotedSong;
    }

    /// <summary>
    /// Registers a song whose file name is shared by several files of the library (so lazily tagging it
    /// later would be ambiguous - the worker could not know which copy the row belongs to). The caller
    /// has already read the tags of THIS file and passes them in; the row is only ever merged with an
    /// EXACT identity match (same file name AND tags), never with a same-named different-tag row. That
    /// way two same-named files with different tags become two distinct, correctly tagged rows from the
    /// very first scan - independent of login or pulls.
    /// </summary>
    public UpvotedSong RegisterUpvotedSongWithTags([StringSyntax(StringSyntaxAttribute.Uri)] string songPath, string album, string artist)
    {
        UpvotedSong newUpvotedSong;
        bool wasInserted = false;
        lock (songRegistrationLock)
        {
            using var dbContext = DbWrapper.GetContext();
            var identityMatch = dbContext.GetUpvotedSongByTags(Path.GetFileName(songPath), artist, album);
            if (identityMatch != null)
                newUpvotedSong = identityMatch;
            else
            {
                newUpvotedSong = dbContext.AddNewUpvotedSong(songPath, (album, artist));
                wasInserted = true;
            }
        }

        if (wasInserted)
            SyncService.UploadNewSongEntry(newUpvotedSong);

        return newUpvotedSong;
    }

    public void UpvoteSong(AvailableSong songToUpvote, List<AvailableSong> AvailableSongs)
    {
        using var dbContext = DbWrapper.GetContext();
        var upvotedSong = dbContext.GetUpvotedSongById(songToUpvote.UpvotedSongId);

        var totalPlayProgress = (AudioLibWrapper.PlayProgress ?? throw new InvalidDataException($"{nameof(AudioLibWrapper.PlayProgress)} is null!"))
            - AudioLibWrapper.SeekedPlayProgress;

        if (upvotedSong.Score > 120)
            upvotedSong.Score = 120;
        if (upvotedSong.Score < -1)
            upvotedSong.Score = -1;

        if (upvotedSong.Streak < 1)
            upvotedSong.Streak = 1;
        else if (totalPlayProgress > 0.9)
            upvotedSong.Streak++;

        var scoreChange = upvotedSong.Streak * GetUpvoteWeight(upvotedSong.Score) * totalPlayProgress * 8;
        upvotedSong.Score += scoreChange;
        upvotedSong.TotalLikes++;

        SaveScoreChange(upvotedSong, scoreChange);

        SongChoosingService.UpdateSongChoosingDataStructure(songToUpvote, AvailableSongs);

        SongGotUpvoted?.Invoke(this, false);
    }
    public void DownvoteSong(AvailableSong songToDownvote, List<AvailableSong> AvailableSongs)
    {
        using var dbContext = DbWrapper.GetContext();
        var upvotedSong = dbContext.GetUpvotedSongById(songToDownvote.UpvotedSongId);

        var totalPlayProgress = (AudioLibWrapper.PlayProgress ?? throw new InvalidDataException($"{nameof(AudioLibWrapper.PlayProgress)} is null!"))
            + AudioLibWrapper.SeekedPlayProgress;

        if (upvotedSong.Score > 120)
            upvotedSong.Score = 120;
        if (upvotedSong.Score < -1)
            upvotedSong.Score = -1;

        if (upvotedSong.Streak > -1)
            upvotedSong.Streak = -1;
        else
            upvotedSong.Streak -= 1;

        var scoreChange = upvotedSong.Streak * GetDownvoteWeight(upvotedSong.Score) * (1 - totalPlayProgress) * 32;
        upvotedSong.Score += scoreChange;
        upvotedSong.TotalDislikes++;

        SaveScoreChange(upvotedSong, scoreChange);

        SongChoosingService.UpdateSongChoosingDataStructure(songToDownvote, AvailableSongs);

        SongGotDownvoted?.Invoke(this, false);
    }

    void SaveScoreChange(UpvotedSong upvotedSong, float scoreChange)
    {
        using var dbContext = DbWrapper.GetContext();
        var newEntry = dbContext.AddNewSongHistoryEntry(upvotedSong.SongId, scoreChange);

        SyncService.Vote(newEntry);
    }

    float GetUpvoteWeight(float SongScore)
    {
        return (float)Math.Pow(2, -SongScore / 20);
    }
    float GetDownvoteWeight(float SongScore)
    {
        return (float)Math.Pow(2, (SongScore - 100) / 20);
    }
}