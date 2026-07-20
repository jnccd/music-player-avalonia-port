using Avalonia.Diagnostics;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongPlaybackService))]
public class SongPlaybackService
{
    readonly AudioLibWrapperService AudioLibWrapper;
    readonly SongVotingService SongVotingService;
    readonly SongChoosingService SongChoosingService;
    readonly DbWrapperService DbWrapper;

    readonly List<AvailableSong> AvailableSongs = [];

    int RuntimePlayHistoryIndex = 0;
    readonly List<AvailableSong> RuntimePlayHistory = [];
    public AvailableSong? CurrentlyPlaying => RuntimePlayHistoryIndex >= 0 && RuntimePlayHistoryIndex < RuntimePlayHistory.Count ?
        RuntimePlayHistory[RuntimePlayHistoryIndex] :
        null;
    public bool UpvoteLockedIn
    {
        get;
        set
        {
            field = value;
            UpvoteLockedInChanged?.Invoke(this, value);
        }
    } = false;

    public event EventHandler<AvailableSong>? NewSongStarted;
    public event EventHandler<bool>? UpvoteLockedInChanged;

    public SongPlaybackService(AudioLibWrapperService AudioLibWrapper, SongVotingService UpvotedSongManager, SongChoosingService SongChoosingService, DbWrapperService DbWrapper)
    {
        this.AudioLibWrapper = AudioLibWrapper;
        AudioLibWrapper.PlaybackEnded += (sender, args) =>
        {
            GetNextSong();
        };

        this.SongVotingService = UpvotedSongManager;
        this.SongChoosingService = SongChoosingService;
        this.DbWrapper = DbWrapper;
    }

    public void UpdateAvailableSongPaths(string libraryRootPath)
    {
        AvailableSongs.Clear();
        AvailableSongs.AddRange([.. HelperFuncs.FindAllMp3FilesInDir(libraryRootPath)
            .Select(CreateAvailableSong)]);

        SongChoosingService.CreateSongChoosingDataStructure(AvailableSongs);
    }
    AvailableSong CreateAvailableSong(string fullPath)
    {
        using var dbContext = DbWrapper.GetContext();
        var upvotedSong = dbContext.GetUpvotedSongByFullPath(fullPath);
        upvotedSong ??= SongVotingService.RegisterNewUpvotedSong(fullPath);

        return new AvailableSong(fullPath, upvotedSong.SongId);
    }
    public AvailableSong? RegisterNewSong(string fullPath)
    {
        var newAvailableSong = CreateAvailableSong(fullPath);
        AvailableSongs.Add(newAvailableSong);

        SongChoosingService.CreateSongChoosingDataStructure(AvailableSongs);

        return newAvailableSong;
    }

    public void PlaySpecificSong(AvailableSong availableSong)
    {
        // Update RuntimePlayHistory
        RuntimePlayHistory.Add(availableSong);
        RuntimePlayHistoryIndex = RuntimePlayHistory.Count - 1;

        // Invoke Events
        AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"));
        NewSongStarted?.Invoke(this, CurrentlyPlaying);
    }
    public void GetNextSong()
    {
        // Score Change
        if (UpvoteLockedIn)
        {
            SongVotingService.UpvoteSong(CurrentlyPlaying
                ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                AvailableSongs);
            UpvoteLockedIn = false;
        }
        else if (RuntimePlayHistoryIndex > 0 && RuntimePlayHistoryIndex == RuntimePlayHistory.Count - 1) // Last Song in filled RuntimePlayHistory
        {
            SongVotingService.DownvoteSong(CurrentlyPlaying
                ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                AvailableSongs);
        }

        // Update RuntimePlayHistory
        RuntimePlayHistoryIndex++;
        while (RuntimePlayHistoryIndex >= RuntimePlayHistory.Count)
        {
            var nextSong = ChooseNextSong();
            RuntimePlayHistory.Add(nextSong);
        }

        // Invoke Events
        AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"));
        NewSongStarted?.Invoke(this, CurrentlyPlaying);
    }
    public void GetPreviousSong()
    {
        // Score Change
        if (UpvoteLockedIn)
        {
            SongVotingService.UpvoteSong(CurrentlyPlaying
                ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                AvailableSongs);
            UpvoteLockedIn = false;
        }

        // Update RuntimePlayHistory
        RuntimePlayHistoryIndex--;
        while (RuntimePlayHistoryIndex < 0)
        {
            var newPreviousSong = ChooseNextSong();
            RuntimePlayHistory.Insert(0, newPreviousSong);
            RuntimePlayHistoryIndex++;
        }

        // Invoke Events
        AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"));
        NewSongStarted?.Invoke(this, CurrentlyPlaying);
    }

    AvailableSong ChooseNextSong()
    {
        var newSong = SongChoosingService.ChooseSongWithWeightedChances(CurrentlyPlaying);
        return newSong;
    }
}