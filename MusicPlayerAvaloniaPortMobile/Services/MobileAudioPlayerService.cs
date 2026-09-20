using MusicPlayerAvaloniaPort;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;
using SoundFlow.Structs;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPortMobile.Services;

/// <summary>
/// The mobile playback backend: SoundFlow/MiniAudio playing a song file, with the loudness measurement the
/// volume normalization needs.
/// <para>
/// What it deliberately does <b>not</b> have is the desktop client's sample-reading machinery: no FFT
/// spectrum analysis, no diagram window, no full-song sample array kept in memory for visualization. Those
/// are the desktop client's most expensive features and no phone music player shows them - on mobile the
/// CPU budget belongs to playback and battery life instead.
/// </para>
/// <para>
/// Measuring a song (for volume normalization) is therefore done differently as well: instead of filling a
/// flat sample array with the whole decoded song, the file is streamed through a second decoder in small
/// chunks and only the sum of squares is accumulated, so the memory cost stays constant no matter how long
/// the song is. The result is published through <see cref="CurrentSongRootMeanSquare"/> /
/// <see cref="MeasuredSongPath"/> and announced with <see cref="FinishedReading"/>, which is exactly the
/// contract the shared <c>SongVolumeService</c> expects.
/// </para>
/// <para>
/// Two hard lessons from the first run on a real phone shape this class:
/// </para>
/// <list type="number">
/// <item>Android's device enumeration does not reliably mark a default playback device, and a
/// <c>DeviceInfo</c> that was not found is a <c>default</c> struct with a <b>null</b>
/// <c>SupportedDataFormats</c>. The first version asked for the default device and then indexed its format
/// list - which threw <see cref="ArgumentNullException"/> inside the constructor and took the whole app
/// down at startup. The device is now optional (a null device means "let miniaudio pick the system
/// default", which is what a phone wants anyway) and the format falls back to a standard 48 kHz stereo
/// F32.</item>
/// <item>Audio setup must never be fatal. The engine and the output device are created on first use,
/// inside a try/catch, and a failure is reported through <see cref="IsAvailable"/> /
/// <see cref="LastError"/> so the UI can say "no audio output" instead of the app dying on the launch
/// screen.</item>
/// </list>
/// </summary>
[RegisterImplementation(ServiceRegisterType.Singleton, typeof(MobileAudioPlayerService))]
public class MobileAudioPlayerService : IAudioPlaybackService
{
    /// <summary>
    /// Number of interleaved samples read per decoder pass while measuring a song. Mirrors the desktop
    /// sample reader's chunk size - big enough to keep the decoder busy, small enough to stay irrelevant
    /// for memory.
    /// </summary>
    const int MEASUREMENT_BUFFER_32BIT_FLOAT_SIZE = 4096;

    /// <summary>
    /// Format used when the device does not report one (see the class remarks). 48 kHz stereo F32 is what
    /// Android audio sinks use natively, so miniaudio only has to resample the decoded song, not the
    /// device stream.
    /// </summary>
    static readonly AudioFormat FallbackFormat = new()
    {
        Format = SampleFormat.F32,
        Channels = 2,
        Layout = ChannelLayout.Stereo,
        SampleRate = 48000
    };

    static AudioEngine? engine;

    AudioPlaybackDevice? playbackDevice;
    SoundPlayer? soundPlayer = null;
    StreamDataProvider? playerDataProvider = null;
    AudioFormat playBackFormat = FallbackFormat;

    /// <summary>Cancels the loudness measurement of the song that was playing before the current one.</summary>
    CancellationTokenSource? measurementCancellation;

    /// <summary>True once the audio engine and an output device exist.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Why playback is unavailable, or null while it works. The view shows it in the status line, so a
    /// device without a usable audio output explains itself instead of looking broken.
    /// </summary>
    public string? LastError { get; private set; }

    // ---------- IAudioPlaybackService ----------

    /// <summary>[0,1] playback volume; the volume service writes the normalized value here.</summary>
    public float Volume
    {
        get;
        set
        {
            soundPlayer?.Volume = value;
            field = value;
        }
    } = 0;

    /// <summary>
    /// Playback position in [0,1]. Setting it seeks and adds the skipped amount to
    /// <see cref="SeekedPlayProgress"/>, because the vote scoring has to know whether a song was listened
    /// through or skipped.
    /// </summary>
    public float? PlayProgress
    {
        get => soundPlayer?.Time / soundPlayer?.Duration;
        set
        {
            if (value == null)
                return;

            // Without a known duration there is nothing to seek to (the decoder may still be opening the
            // file); seeking must not corrupt the skipped-progress accounting the vote scoring uses.
            if (soundPlayer?.Duration is not float duration || duration <= 0)
                return;

            float currentProgress = PlayProgress ?? 0;
            if (float.IsNaN(currentProgress) || float.IsInfinity(currentProgress))
                currentProgress = 0;

            SeekedPlayProgress += value.Value - currentProgress;
            soundPlayer.Seek(value.Value * duration);
        }
    }

    public float? SongDurationSeconds => soundPlayer?.Duration;

    public float SeekedPlayProgress { get; private set; } = 0;

    public float? CurrentSongRootMeanSquare { get; private set; }

    public string? MeasuredSongPath { get; private set; }

    public event EventHandler<EventArgs>? PlaybackEnded;
    public event EventHandler<EventArgs>? FinishedReading;

    /// <summary>Current transport state, for the play/pause button of the view.</summary>
    public PlaybackState? PlayState => soundPlayer?.State;

    /// <summary>Raised whenever the transport state changed (play, pause, song switch).</summary>
    public event EventHandler<PlaybackState>? PlaybackStateChanged;

    public void PlaySong(string songPath, bool measureWholeSongForVolumeNormalization)
    {
        // A new song: the previous measurement (and its background read) is meaningless now.
        CancelMeasurement();
        CurrentSongRootMeanSquare = null;
        MeasuredSongPath = null;

        try
        {
            EnsureAudioOutput();

            playerDataProvider?.Dispose();
            playerDataProvider = new StreamDataProvider(engine!, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });

            if (soundPlayer != null)
            {
                playbackDevice!.MasterMixer.RemoveComponent(soundPlayer);
                soundPlayer.Dispose();
                soundPlayer = null;
                playbackDevice.Dispose();
                playbackDevice = null;
            }

            playbackDevice = engine!.InitializePlaybackDevice(ResolvePlaybackDevice(engine), GetCurrentAudioFormat());
            soundPlayer = new SoundPlayer(engine, GetCurrentAudioFormat(), playerDataProvider);
            playbackDevice.MasterMixer.AddComponent(soundPlayer);
            playbackDevice.Start();
            soundPlayer.Volume = Volume;
            soundPlayer.Play();
            SeekedPlayProgress = 0;

            soundPlayer.PlaybackEnded += SoundPlayer_PlaybackEnded;

            if (measureWholeSongForVolumeNormalization)
                StartMeasurement(songPath);

            // A song is playing, so the ongoing media notification belongs on screen - and with it the
            // foreground service that keeps playback alive when the app is left. Android 12+ forbids starting
            // a foreground service from the background, so this is best effort and logs instead of throwing;
            // the first song always starts from the UI, which is a legal moment, and later song changes only
            // update a service that is already running.
            MobilePlaybackNotificationService.Start(MobilePlatform.ApplicationContext);

            Task.Run(() => PlaybackStateChanged?.Invoke(this, PlayState ?? PlaybackState.Stopped));
        }
        catch (Exception ex)
        {
            // Never let audio setup escape into the caller: the shared playback service and the view model
            // would take the whole app down with it (that is exactly what happened on the first phone run).
            Fail($"Cannot play audio: {ex.Message}", ex);
        }
    }

    public void TogglePlayPause(bool updateAudioDevicesInfo = false)
    {
        try
        {
            if (soundPlayer == null)
                return;

            if (soundPlayer.State == PlaybackState.Playing)
                soundPlayer.Pause();
            else
                soundPlayer.Play();

            Task.Run(() => PlaybackStateChanged?.Invoke(this, PlayState ?? PlaybackState.Stopped));
        }
        catch (Exception ex)
        {
            Fail($"Cannot toggle playback: {ex.Message}", ex);
        }
    }

    // ---------- audio output ----------

    /// <summary>
    /// Creates the MiniAudio engine and the output format description on first use. Guarded and non-fatal:
    /// a device whose audio stack cannot be initialised keeps the app usable and reports why.
    /// </summary>
    void EnsureAudioOutput()
    {
        if (IsAvailable)
            return;

        engine ??= new MiniAudioEngine();

        // The engine reporting no devices at all is a real failure worth naming, but note that it is legal
        // for devices to exist without one of them being flagged as the default - which is why the device
        // resolved below may be null.
        DeviceInfo? preferred = ResolvePlaybackDevice(engine);
        playBackFormat = preferred is { SupportedDataFormats.Length: > 0 } device
            ? AudioFormat.GetFormatFromNativeFormat(device.SupportedDataFormats[0])
            : FallbackFormat;

        LastError = null;
        IsAvailable = true;
        MobileLog.Info($"Audio output ready (playback devices: {engine.PlaybackDevices.Length}, device: {preferred?.Name ?? "<system default>"}, rate: {playBackFormat.SampleRate}, channels: {playBackFormat.Channels})");
    }

    /// <summary>
    /// The output device to open: the one the platform flagged as default, otherwise the first one that
    /// reports a usable format, otherwise <c>null</c> - and null simply means "let miniaudio open the system
    /// default", which is the right answer on a phone.
    /// </summary>
    static DeviceInfo? ResolvePlaybackDevice(AudioEngine audioEngine)
    {
        var devices = audioEngine.PlaybackDevices;
        if (devices is not { Length: > 0 })
            return null;

        var flaggedDefault = devices.FirstOrDefault(device => device.IsDefault);
        if (flaggedDefault.Name != null)
            return flaggedDefault;

        var withFormat = devices.FirstOrDefault(device => device.SupportedDataFormats is { Length: > 0 });
        return withFormat.Name != null ? withFormat : null;
    }

    AudioFormat GetCurrentAudioFormat() => new()
    {
        Channels = playerDataProvider?.FormatInfo?.ChannelCount ?? playBackFormat.Channels,
        Layout = AudioFormat.GetLayoutFromChannels(playerDataProvider?.FormatInfo?.ChannelCount ?? playBackFormat.Channels),
        Format = playBackFormat.Format,
        SampleRate = playerDataProvider?.FormatInfo?.SampleRate ?? playBackFormat.SampleRate
    };

    void Fail(string message, Exception ex)
    {
        LastError = message;
        IsAvailable = false;
        MobileLog.Error(message, ex);
    }

    // ---------- internals ----------

    void SoundPlayer_PlaybackEnded(object? sender, EventArgs e)
    {
        // Like the desktop client: only a song that actually played (nearly) to its end advances the
        // runtime history - a decoder hiccup at the very beginning must not look like a finished song.
        if (PlayProgress > 0.9)
            Task.Run(() => PlaybackEnded?.Invoke(this, EventArgs.Empty));
    }

    void CancelMeasurement()
    {
        try
        {
            measurementCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The measurement already finished and disposed its source; nothing to cancel.
        }
        measurementCancellation = null;
    }

    /// <summary>
    /// Measures the loudness of a song in the background by streaming it through its own decoder and
    /// accumulating the sum of squares (constant memory, unlike the desktop client's flat sample array).
    /// The result is published only when the measurement was neither cancelled (song switch) nor hit by an
    /// error - a half-read song would report a wrong loudness and normalize it forever.
    /// </summary>
    void StartMeasurement(string songPath)
    {
        if (engine == null)
            return;

        var cancellation = new CancellationTokenSource();
        measurementCancellation = cancellation;

        Task.Run(() =>
        {
            var token = cancellation.Token;
            try
            {
                using var stream = new FileStream(songPath, FileMode.Open, FileAccess.Read);
                using var provider = new StreamDataProvider(engine, stream, new ReadOptions { ReadTags = false });

                var buffer = ArrayPool<float>.Shared.Rent(MEASUREMENT_BUFFER_32BIT_FLOAT_SIZE);
                try
                {
                    double sumOfSquares = 0;
                    long sampleCount = 0;
                    int framesRead;
                    while (!token.IsCancellationRequested &&
                        (framesRead = provider.ReadBytes(buffer.AsSpan(0, MEASUREMENT_BUFFER_32BIT_FLOAT_SIZE))) > 0)
                    {
                        for (int i = 0; i < framesRead; i++)
                            sumOfSquares += (double)buffer[i] * buffer[i];
                        sampleCount += framesRead;
                    }

                    if (token.IsCancellationRequested || sampleCount == 0)
                        return;

                    MeasuredSongPath = songPath;
                    CurrentSongRootMeanSquare = (float)Math.Sqrt(sumOfSquares / sampleCount);
                    MobileLog.Info($"Measured \"{songPath}\": RMS {CurrentSongRootMeanSquare:F5} over {sampleCount} samples.");

                    FinishedReading?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(buffer);
                }
            }
            catch (Exception ex)
            {
                // Measuring is best effort: without it the song simply plays at the plain user volume and is
                // measured again the next time it is played.
                MobileLog.Warn($"Could not measure \"{songPath}\": {ex.Message}");
            }
        }, cancellation.Token);
    }
}
