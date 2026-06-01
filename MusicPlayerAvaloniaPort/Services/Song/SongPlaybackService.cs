using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongPlaybackService))]
public class SongPlaybackService
{
    readonly AudioLibWrapperService AudioLibWrapper;
    readonly UpvotedSongManagerService UpvotedSongManager;
    readonly SongChoosingService SongChoosingService;

    readonly List<AvailableSong> AvailableSongs = [];

    int RuntimePlayHistoryIndex = 0;
    readonly List<AvailableSong> RuntimePlayHistory = [];
    AvailableSong? CurrentlyPlaying => RuntimePlayHistoryIndex >= 0 && RuntimePlayHistoryIndex < RuntimePlayHistory.Count ?
        RuntimePlayHistory[RuntimePlayHistoryIndex] :
        null;
    public bool UpvoteLockedIn { get; set; } = false;

    public event EventHandler<AvailableSong>? NewSongStarted;

    public SongPlaybackService(AudioLibWrapperService AudioLibWrapper, UpvotedSongManagerService UpvotedSongManager, SongChoosingService SongChoosingService)
    {
        this.AudioLibWrapper = AudioLibWrapper;
        AudioLibWrapper.PlaybackEnded += (sender, args) =>
        {
            GetNextSong();
        };

        this.UpvotedSongManager = UpvotedSongManager;
        this.SongChoosingService = SongChoosingService;
    }

    public void UpdateAvailableSongPaths(string libraryRootPath)
    {
        AvailableSongs.Clear();
        using var songDbContext = new SongDbContext();
        AvailableSongs.AddRange([.. HelperFuncs.FindAllMp3FilesInDir(libraryRootPath)
            .Select(path => CreateAvailableSong(path))]);

        SongChoosingService.CreateSongChoosingDataStructure(AvailableSongs);
    }

    AvailableSong CreateAvailableSong(string path)
    {
        using var songDbContext = new SongDbContext();

        var upvotedSong = UpvotedSongManager.FindUpvotedSong(path);
        upvotedSong ??= UpvotedSongManager.RegisterNewUpvotedSong(path);

        return new AvailableSong(path, upvotedSong.SongId);
    }

    public void GetNextSong()
    {
        // Score Change
        if (UpvoteLockedIn)
        {
            UpvotedSongManager.UpvoteUpvotedSong(CurrentlyPlaying
                ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                AvailableSongs);
            UpvoteLockedIn = false; // TODO: Callback to ui so its also updated there
        }
        else if (RuntimePlayHistoryIndex > 0 && RuntimePlayHistoryIndex == RuntimePlayHistory.Count - 1) // Last Song in filled RuntimePlayHistory
        {
            UpvotedSongManager.DownvoteUpvotedSong(CurrentlyPlaying
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
            UpvotedSongManager.UpvoteUpvotedSong(CurrentlyPlaying
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