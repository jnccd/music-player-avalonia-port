using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services;

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
    AvailableSong? CurrentlyPlaying => RuntimePlayHistoryIndex >= 0 && RuntimePlayHistoryIndex < RuntimePlayHistory.Count ?
        RuntimePlayHistory[RuntimePlayHistoryIndex] :
        null;
    public bool UpvoteLockedIn { get; set; } = false;

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

    public void GetNextSong()
    {
        // Score Change
        if (UpvoteLockedIn)
        {
            SongVotingService.UpvoteSong(CurrentlyPlaying
                ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                AvailableSongs);
            UpvoteLockedIn = false;
            UpvoteLockedInChanged?.Invoke(this, UpvoteLockedIn);
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
            UpvoteLockedInChanged?.Invoke(this, UpvoteLockedIn);
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