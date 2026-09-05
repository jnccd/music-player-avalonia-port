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
        var currentUpvotedSong = dbWrapperService.GetContext().GetUpvotedSongByIdOrNull(currentSong.UpvotedSongId);
        if (currentUpvotedSong == null)
            return false; // The row vanished (e.g. a pull replaced the local rows) - keep the plain volume

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

    /// <summary>
    /// Resets the stored volume multiplier of a song to "unknown" (-1), like the DxMGP statistics
    /// context menu did. The next time the song is read it is measured and stored again automatically
    /// (see <see cref="SetCurrentSongsVolumeIfNecessary"/>). If the song is playing right now its
    /// audio volume is switched back to the plain user volume immediately.
    /// </summary>
    public void ResetVolumeMultiplier(Guid songId)
    {
        using var dbContext = dbWrapperService.GetContext();
        var currentUpvotedSong = dbContext.GetUpvotedSongById(songId);

        currentUpvotedSong.Volume = -1;
        dbContext.SaveChanges();

        if (songPlaybackService.CurrentlyPlaying?.UpvotedSongId == songId)
            UpdateAudioLibVolume();
    }

    private void SetCurrentSongsVolumeIfNecessary()
    {
        var currentSong = songPlaybackService.CurrentlyPlaying;
        if (currentSong == null)
            return;
        var dbContext = dbWrapperService.GetContext();
        var currentUpvotedSong = dbContext.GetUpvotedSongByIdOrNull(currentSong.UpvotedSongId);
        if (currentUpvotedSong == null)
            return; // The row vanished (e.g. a pull replaced the local rows) - nothing to measure against

        if (currentUpvotedSong.Volume > 0) // Not necessary
            return;

        var samples = audioLibWrapperService.GetCurrentSongEntireSampleData();

        if (samples == null) // Not possible
            return;

        var rms = ComputeRootMeanSquare(samples);

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