using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort;

public class SongManager
{
    public List<string> AvailableSongPaths = new();

    public int RuntimePlayHistoryIndex = 0;
    public List<string> RuntimePlayHistorySongPaths = new();
    public string CurrentlyPlaying => RuntimePlayHistorySongPaths.LastOrDefault() ?? string.Empty;

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