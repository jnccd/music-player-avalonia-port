using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongManagerService))]
public class SongManagerService
{
    readonly AudioLibWrapperService AudioLibWrapper;
    readonly UpvotedSongManagerService UpvotedSongManager;

    readonly List<AvailableSong> AvailableSongs = [];

    int RuntimePlayHistoryIndex = 0;
    readonly List<AvailableSong> RuntimePlayHistory = [];
    AvailableSong? CurrentlyPlaying => RuntimePlayHistoryIndex >= 0 && RuntimePlayHistoryIndex < RuntimePlayHistory.Count ?
        RuntimePlayHistory[RuntimePlayHistoryIndex] :
        null;

    public event EventHandler<AvailableSong>? NewSongStarted;

    public SongManagerService(AudioLibWrapperService AudioLibWrapper, UpvotedSongSyncService SyncService, UpvotedSongManagerService UpvotedSongManager)
    {
        this.AudioLibWrapper = AudioLibWrapper;
        AudioLibWrapper.PlaybackEnded += (sender, args) =>
        {
            GetNextSong();
        };

        this.UpvotedSongManager = UpvotedSongManager;
    }

    public void UpdateAvailableSongPaths(string libraryRootPath)
    {
        AvailableSongs.Clear();
        using var songDbContext = new SongDbContext();
        AvailableSongs.AddRange([.. HelperFuncs.FindAllMp3FilesInDir(libraryRootPath)
            .Select(path => CreateAvailableSong(path, songDbContext))]);
    }

    AvailableSong CreateAvailableSong(string path, SongDbContext? songDbContext)
    {
        songDbContext ??= new SongDbContext();

        var upvotedSong = UpvotedSongManager.FindUpvotedSong(path, songDbContext);
        upvotedSong ??= UpvotedSongManager.RegisterNewUpvotedSong(path);

        return new AvailableSong(path, upvotedSong.SongId);
    }

    public void GetNextSong()
    {
        RuntimePlayHistoryIndex++;
        while (RuntimePlayHistoryIndex >= RuntimePlayHistory.Count)
        {
            var nextSong = ChooseNextSong();
            RuntimePlayHistory.Add(nextSong);
        }

        AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"));
        NewSongStarted?.Invoke(this, CurrentlyPlaying);
    }
    public void GetPreviousSong()
    {
        RuntimePlayHistoryIndex--;
        while (RuntimePlayHistoryIndex < 0)
        {
            var newPreviousSong = ChooseNextSong();
            RuntimePlayHistory.Insert(0, newPreviousSong);
            RuntimePlayHistoryIndex++;
        }

        AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"));
        NewSongStarted?.Invoke(this, CurrentlyPlaying);
    }

    AvailableSong ChooseNextSong()
    {
        // TODO Proper Song Choosing
        var newSong = AvailableSongs[Random.Shared.Next(AvailableSongs.Count)];
        return newSong;
    }
}