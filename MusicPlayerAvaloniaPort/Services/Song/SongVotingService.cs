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

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongVotingService))]
public class SongVotingService(AudioLibWrapperService AudioLibWrapper, UpvotedSongSyncService SyncService, SongChoosingService SongChoosingService, DbWrapperService DbWrapper)
{
    public UpvotedSong RegisterNewUpvotedSong([StringSyntax(StringSyntaxAttribute.Uri)] string songPath)
    {
        using var dbContext = DbWrapper.GetContext();
        var newUpvotedSong = dbContext.AddNewUpvotedSong(songPath);

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

        // TODO: Show ui popup?
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

        // TODO: Show ui popup? Program.game.ShowSecondRowMessage("Downvoted  previous  song!", 1.2f);
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