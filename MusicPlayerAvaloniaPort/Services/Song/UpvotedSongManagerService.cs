using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(UpvotedSongManagerService))]
public class UpvotedSongManagerService(AudioLibWrapperService AudioLibWrapper, UpvotedSongSyncService SyncService, SongChoosingService SongChoosingService)
{
    public UpvotedSong RegisterNewUpvotedSong([StringSyntax(StringSyntaxAttribute.Uri)] string songPath)
    {
        var songFileName = Path.GetFileName(songPath);
        var newUpvotedSong = new UpvotedSong(songFileName, 0, 0, 0, 0, GetSongAgeFromPath(songPath), -1) { Path = songPath };

        using var songDbContext = new SongDbContext();
        songDbContext.UpvotedSongs.Add(newUpvotedSong);
        songDbContext.SaveChanges();

        SyncService.UploadNewSong(newUpvotedSong);

        return newUpvotedSong;
    }

    public UpvotedSong? FindUpvotedSong([StringSyntax(StringSyntaxAttribute.Uri)] string songPath)
    {
        using var songDbContext = new SongDbContext();

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

    public void UpvoteUpvotedSong(AvailableSong songToUpvote, List<AvailableSong> AvailableSongs)
    {
        using var songDbContext = new SongDbContext();
        var upvotedSong = songDbContext.UpvotedSongs.FirstOrDefault(x => x.SongId == songToUpvote.UpvotedSongId)
            ?? throw new ArgumentException("No matching UpvotedSongs for guid", nameof(songToUpvote.UpvotedSongId));

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

        SaveScoreChangeToHistory(upvotedSong, scoreChange);

        SongChoosingService.UpdateSongChoosingDataStructure(songToUpvote, AvailableSongs);

        // TODO: Show ui popup?
    }
    public void DownvoteUpvotedSong(AvailableSong songToDownvote, List<AvailableSong> AvailableSongs)
    {
        using var songDbContext = new SongDbContext();
        var upvotedSong = songDbContext.UpvotedSongs.FirstOrDefault(x => x.SongId == songToDownvote.UpvotedSongId)
            ?? throw new ArgumentException("No matching UpvotedSongs for guid", nameof(songToDownvote.UpvotedSongId));

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

        SaveScoreChangeToHistory(upvotedSong, scoreChange);

        SongChoosingService.UpdateSongChoosingDataStructure(songToDownvote, AvailableSongs);

        // TODO: Show ui popup? Program.game.ShowSecondRowMessage("Downvoted  previous  song!", 1.2f);
    }

    void SaveScoreChangeToHistory(UpvotedSong upvotedSong, float scoreChange)
    {
        using var songDbContext = new SongDbContext();

        var newEntry = new SongHistoryEntry(upvotedSong.SongId, scoreChange, DateTime.Now);
        songDbContext.SongHistoryEntries.Add(newEntry);
        songDbContext.SaveChanges();

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