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
    List<string> AvailableSongPaths = new();

    int RuntimePlayHistoryIndex = 0;
    List<string> RuntimePlayHistorySongPaths = new();
    string CurrentlyPlaying => RuntimePlayHistorySongPaths.LastOrDefault() ?? string.Empty;

    public void UpdateAvailableSongPaths(string libraryRootPath)
    {
        AvailableSongPaths = HelperFuncs.FindAllMp3FilesInDir(libraryRootPath);
    }

    public void GetNextSong(Action<string> initPlayingCurrentSong)
    {
        RuntimePlayHistoryIndex++;
        while (RuntimePlayHistoryIndex >= RuntimePlayHistorySongPaths.Count)
        {
            var nextSong = ChooseNewSongPath();
            RuntimePlayHistorySongPaths.Add(nextSong);
        }
        initPlayingCurrentSong(CurrentlyPlaying);
    }
    public void GetPreviousSong(Action<string> initPlayingCurrentSong)
    {
        RuntimePlayHistoryIndex--;
        while (RuntimePlayHistoryIndex < 0)
        {
            var newPreviousSong = ChooseNewSongPath();
            RuntimePlayHistorySongPaths.Insert(0, newPreviousSong);
            RuntimePlayHistoryIndex++;
        }
        initPlayingCurrentSong(CurrentlyPlaying);
    }

    public string ChooseNewSongPath()
    {
        // TODO Proper Song Choosing
        var newSong = AvailableSongPaths[Random.Shared.Next(AvailableSongPaths.Count)];
        return newSong;
    }
}