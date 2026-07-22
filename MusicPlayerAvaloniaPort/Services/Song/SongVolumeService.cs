using Avalonia.Diagnostics;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongVolumeService))]
public class SongVolumeService
{
    AudioLibWrapperService audioLibWrapperService;
    SongPlaybackService songPlaybackService;
    DbWrapperService dbWrapperService;

    public float UserDefinedVolume
    {
        get;
        set
        {
            field = value;
            UpdateAudioLibVolume();
            Config.Data.Volume = value;
            UserDefinedVolumeChanged?.Invoke(this, value);
        }
    } = Config.Data.Volume;
    const float BASE_VOLUME = 0.12f;

    public event EventHandler<float>? UserDefinedVolumeChanged;

    public SongVolumeService(AudioLibWrapperService audioLibWrapperService, SongPlaybackService songPlaybackService, DbWrapperService dbWrapperService)
    {
        this.audioLibWrapperService = audioLibWrapperService;
        this.songPlaybackService = songPlaybackService;
        this.dbWrapperService = dbWrapperService;

        songPlaybackService.NewSongStarted += (e, s) =>
        {
            var hadDbVolumeData = UpdateAudioLibVolume();
        };

        audioLibWrapperService.FinishedReading += (s, e) =>
        {
            SetCurrentSongsVolumeIfNecessary();
        };
    }

    private bool UpdateAudioLibVolume()
    {
        var currentSong = songPlaybackService.CurrentlyPlaying;
        if (currentSong == null)
            return false;
        var currentUpvotedSong = dbWrapperService.GetContext().GetUpvotedSongById(currentSong?.UpvotedSongId);

        if (currentUpvotedSong.Volume > 0)
        {
            var volumeMultiplier = BASE_VOLUME / currentUpvotedSong.Volume;
            Debug.WriteLine($"Applying volume multiplier of {volumeMultiplier}");
            audioLibWrapperService.Volume = UserDefinedVolume * volumeMultiplier;
        }
        else
        {
            audioLibWrapperService.Volume = UserDefinedVolume;
        }

        return currentUpvotedSong.Volume > 0;
    }

    private void SetCurrentSongsVolumeIfNecessary()
    {
        var currentSong = songPlaybackService.CurrentlyPlaying;
        var dbContext = dbWrapperService.GetContext();
        var currentUpvotedSong = dbContext.GetUpvotedSongById(currentSong?.UpvotedSongId);

        if (currentUpvotedSong.Volume > 0) // Not necessary
            return;

        var samples = audioLibWrapperService.GetCurrentSongSampleData();

        var rms = ComputeRootMeanSquare(samples!);

        currentUpvotedSong.Volume = rms;
        dbContext.SaveChanges();

        UpdateAudioLibVolume();
    }

    private float ComputeRootMeanSquare(IEnumerable<float> samples)
    {
        float n = 0;

        foreach (float sample in samples)
            n += sample * sample;
        n /= samples.Count();

        float sn = (float)Math.Sqrt(n);

        return sn;
    }
}