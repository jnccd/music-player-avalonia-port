using Avalonia;
using MusicPlayerAvaloniaPort.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongManagerService))]
public class SongManagerService
{
    AudioLibWrapperService AudioLibWrapper;

    List<string> AvailableSongPaths = new();

    int RuntimePlayHistoryIndex = 0;
    List<string> RuntimePlayHistorySongPaths = new();
    string? CurrentlyPlaying => RuntimePlayHistorySongPaths.LastOrDefault();

    public event EventHandler<string>? NewSongStarted;

    public SongManagerService(AudioLibWrapperService AudioLibWrapper)
    {
        this.AudioLibWrapper = AudioLibWrapper;
        AudioLibWrapper.PlaybackEnded += (sender, args) =>
        {
            GetNextSong();
        };
    }

    public void UpdateAvailableSongPaths(string libraryRootPath)
    {
        AvailableSongPaths = HelperFuncs.FindAllMp3FilesInDir(libraryRootPath);
    }

    public void GetNextSong()
    {
        RuntimePlayHistoryIndex++;
        while (RuntimePlayHistoryIndex >= RuntimePlayHistorySongPaths.Count)
        {
            var nextSong = ChooseNewSongPath();
            RuntimePlayHistorySongPaths.Add(nextSong);
        }

        AudioLibWrapper.PlaySong(CurrentlyPlaying ?? throw new InvalidDataException("No song to play"));
        NewSongStarted?.Invoke(this, CurrentlyPlaying);
    }
    public void GetPreviousSong()
    {
        RuntimePlayHistoryIndex--;
        while (RuntimePlayHistoryIndex < 0)
        {
            var newPreviousSong = ChooseNewSongPath();
            RuntimePlayHistorySongPaths.Insert(0, newPreviousSong);
            RuntimePlayHistoryIndex++;
        }

        AudioLibWrapper.PlaySong(CurrentlyPlaying ?? throw new InvalidDataException("No song to play"));
        NewSongStarted?.Invoke(this, CurrentlyPlaying);
    }

    public string ChooseNewSongPath()
    {
        // TODO Proper Song Choosing
        var newSong = AvailableSongPaths[Random.Shared.Next(AvailableSongPaths.Count)];
        return newSong;
    }
}