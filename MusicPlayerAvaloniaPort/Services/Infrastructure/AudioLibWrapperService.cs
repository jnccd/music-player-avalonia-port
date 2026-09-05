using MusicPlayerAvaloniaPort.Persistence.Configuration;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;
using SoundFlow.Structs;
using SoundFlow.Visualization;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

public enum SampleReadingStrategy
{
    GlobalArray,
    DirectRead
}

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(AudioLibWrapperService))]
public class AudioLibWrapperService
{
    private static readonly AudioEngine Engine = new MiniAudioEngine();
    DeviceInfo playbackDeviceInfo;
    AudioPlaybackDevice playbackDevice;
    SoundPlayer? soundPlayer = null;
    StreamDataProvider? playerDataProvider = null;
    private AudioFormat playBackFormat;
    readonly ArrayPool<float> arrayPool = ArrayPool<float>.Shared;

    // Sample Reading
    const int SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE = 4096;
    StreamDataProvider? sampleReaderDataProvider = null;
    private float[]? globalSampleArray = null;
    int globalSampleArrayWriteHead = 0;
    Task? SampleReaderThread = null;
    bool CancelReading = false;
    const int SAMPLE_OUTPUT_BUFFER_32BIT_FLOAT_SIZE = 16384;
    SampleReadingStrategy currentSampleReadingStrategy = SampleReadingStrategy.GlobalArray;
    /// <summary>
    /// Decoded chunks the DirectRead sample strategy keeps across frames so the overlapping part of
    /// the read window is not decoded again. <see cref="frameData"/> is a pooled buffer holding
    /// <see cref="validLength"/> valid floats starting at <see cref="framePosition"/>; buffers are
    /// returned to the pool when their chunk scrolls out of the read zone.
    /// </summary>
    List<(int framePosition, float[] frameData, int validLength)> directReadStrategyReadBuffers = [];
    /// <summary>
    /// Reused scratch the DirectRead strategy assembles its read window in. Only valid until the next
    /// call - every consumer reads it synchronously (see <see cref="GetCurrentlyPlayingSampleData"/>).
    /// </summary>
    float[]? directReadWindowScratch;

    // FFT Vars
    /// <summary>
    /// Number of (float) samples of the read window around the currently playing sample and the FFT
    /// size of the full-resolution frequency analysis. Changing this changes the time span the samples
    /// visualization shows around the playback position, so it must stay the same for both.
    /// </summary>
    public const int FFT_BUFFER_32BIT_FLOAT_SIZE = 16384;
    /// <summary>
    /// FFT size the frequency analysis runs at in low power mode (a power of two, smaller than
    /// <see cref="FFT_BUFFER_32BIT_FLOAT_SIZE"/>). Only the analysis resolution shrinks - the read
    /// window itself keeps its full size, because its time span around the currently playing sample
    /// is what the diagram shows.
    /// </summary>
    public const int FFT_BUFFER_LOW_POWER_SIZE = 4096;
    SpectrumAnalyzer? spectrumAnalyzer;
    int spectrumAnalyzerFftSize;
    float[] fftZeroResult, sampleZeroResult;

    // Setters
    /// <summary>
    /// [0,1]
    /// </summary>
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
    /// [0,1]
    /// </summary>
    public float? PlayProgress
    {
        get => soundPlayer?.Time / soundPlayer?.Duration;
        set
        {
            if (value != null)
            {
                SeekedPlayProgress += value.Value - (PlayProgress
                    ?? throw new InvalidDataException($"{nameof(PlayProgress)} is null!"));
                soundPlayer?.Seek(value.Value * soundPlayer.Duration);
            }
        }
    }
    public float? SongDurationSeconds => soundPlayer?.Duration;
    /// <summary>
    /// Same unit as PlayProgress but may land outside of any bounds due to the nature of what it represents.
    /// Forward seeking results in positive numbers and backwards seeking in negative.
    /// </summary>
    public float SeekedPlayProgress { get; private set; } = 0;
    public PlaybackState? PlayState
    {
        get => soundPlayer?.State;
    }
    public event EventHandler<EventArgs>? PlaybackEnded;
    public event EventHandler<EventArgs>? FinishedReading;
    public event EventHandler<PlaybackState>? PlaybackStateChanged;

    public AudioLibWrapperService()
    {
        fftZeroResult = arrayPool.Rent(FFT_BUFFER_32BIT_FLOAT_SIZE);
        sampleZeroResult = arrayPool.Rent(SAMPLE_OUTPUT_BUFFER_32BIT_FLOAT_SIZE);

        if (Engine.PlaybackDevices.Length == 0)
        {
            throw new InvalidOperationException("No default playback device found.");
        }
        playbackDeviceInfo = Engine.PlaybackDevices.FirstOrDefault(d => d.IsDefault);
        var playbackDeviceInfoFormat = playbackDeviceInfo.SupportedDataFormats.First();
        playBackFormat = AudioFormat.GetFormatFromNativeFormat(playbackDeviceInfoFormat);
        playbackDevice = Engine.InitializePlaybackDevice(playbackDeviceInfo, GetCurrentAudioFormat());
        playbackDevice.Start();
    }

    private void SoundPlayer_PlaybackEnded(object? sender, EventArgs e)
    {
        if (PlayProgress > 0.9)
            Task.Run(() =>
            {
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            });
    }

    private AudioFormat GetCurrentAudioFormat()
    {
        return new AudioFormat()
        {
            Channels = playerDataProvider?.FormatInfo?.ChannelCount ?? 2,
            Layout = AudioFormat.GetLayoutFromChannels((playerDataProvider?.FormatInfo?.ChannelCount) ?? 2),
            Format = playBackFormat.Format,
            SampleRate = playerDataProvider?.FormatInfo?.SampleRate ?? 48000
        };
    }
    private uint GetCurrentPeriodSizeInFrames()
    {
        return (uint?)(playerDataProvider?.FormatInfo?.SampleRate / 100 * playerDataProvider?.FormatInfo?.ChannelCount) ?? 960;
    }

    public void TogglePlayPause(bool UpdateAudioDevicesInfo = false)
    {
        if (soundPlayer == null)
            return;

        if (UpdateAudioDevicesInfo)
        {
            Engine.UpdateAudioDevicesInfo();
            if (playbackDeviceInfo.Name != Engine.PlaybackDevices.First(d => d.IsDefault).Name)
            {
                playbackDevice.Dispose();
                playbackDeviceInfo = Engine.PlaybackDevices.FirstOrDefault(d => d.IsDefault);
                var playbackDeviceInfoFormat = playbackDeviceInfo.SupportedDataFormats.First();
                playBackFormat = AudioFormat.GetFormatFromNativeFormat(playbackDeviceInfoFormat);
                playbackDevice = Engine.InitializePlaybackDevice(playbackDeviceInfo, playBackFormat);
                playbackDevice.MasterMixer.AddComponent(soundPlayer);
                playbackDevice.Start();
            }
        }
        if (soundPlayer.State == PlaybackState.Playing)
            soundPlayer.Pause();
        else
            soundPlayer.Play();

        Task.Run(() =>
        {
            PlaybackStateChanged?.Invoke(this, PlayState!.Value);
        });
    }

    public void PlaySong(string songPath, SampleReadingStrategy sampleReadingStrategy = SampleReadingStrategy.GlobalArray)
    {
        playerDataProvider?.Dispose();
        playerDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });
        sampleReaderDataProvider?.Dispose();
        sampleReaderDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });
        EnsureSpectrumAnalyzer(forceRecreate: true);

        if (soundPlayer != null)
        {
            playbackDevice.MasterMixer.RemoveComponent(soundPlayer);
            soundPlayer.Dispose();
            playbackDevice.Dispose();
        }

        currentSampleReadingStrategy = sampleReadingStrategy;

        playbackDevice = Engine.InitializePlaybackDevice(playbackDeviceInfo, GetCurrentAudioFormat(), new MiniAudioDeviceConfig
        {
            PeriodSizeInFrames = GetCurrentPeriodSizeInFrames()
        });
        soundPlayer = new SoundPlayer(Engine, GetCurrentAudioFormat(), playerDataProvider);
        playbackDevice.MasterMixer.AddComponent(soundPlayer);
        playbackDevice.Start();
        soundPlayer.Volume = Volume;
        soundPlayer.Play();
        SeekedPlayProgress = 0;

        soundPlayer.PlaybackEnded += SoundPlayer_PlaybackEnded;

        if (SampleReaderThread != null)
        {
            CancelReading = true;
            SampleReaderThread.Wait();
        }
        CancelReading = false;
        Debug.WriteLine($"{DateTime.Now:HH:mm:ss.ffff} Starting Reading!");
        if (currentSampleReadingStrategy == SampleReadingStrategy.DirectRead)
        {
            globalSampleArray = null;
            ReleaseDirectReadBuffers();
        }
        if (currentSampleReadingStrategy == SampleReadingStrategy.GlobalArray)
            SampleReaderThread = Task.Run(() =>
            {
                globalSampleArrayWriteHead = 0;
                int requiredGlobalSampleArrayLength = playerDataProvider.Length > 0 ? playerDataProvider.Length : 48000 * 60 * 5;
                globalSampleArray = new float[requiredGlobalSampleArrayLength];
                ReleaseDirectReadBuffers();
                GC.Collect();

                var sampleBuffer = arrayPool.Rent(SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE);
                var sampleBufferSpan = sampleBuffer.AsSpan();
                int framesRead;

                while (!CancelReading &&
                    globalSampleArrayWriteHead + SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE < globalSampleArray.Length &&
                    // Read buffer from audio file
                    (framesRead = sampleReaderDataProvider!.ReadBytes(sampleBufferSpan)) > 0)
                {
                    // Write into global array
                    Buffer.BlockCopy(sampleBuffer, 0, globalSampleArray, globalSampleArrayWriteHead * sizeof(float), framesRead * sizeof(float));
                    //Array.Copy(sampleBuffer, 0, globalSampleArray, globalSampleArrayWriteHead, framesRead);
                    globalSampleArrayWriteHead += framesRead;
                }

                Debug.WriteLine($"{DateTime.Now:HH:mm:ss.ffff} Done Reading!");
                Task.Run(() =>
                {
                    FinishedReading?.Invoke(this, EventArgs.Empty);
                });

                arrayPool.Return(sampleBuffer);
            });

        Task.Run(() =>
        {
            PlaybackStateChanged?.Invoke(this, PlayState!.Value);
        });
    }

    /// <summary>
    /// Returns the decoded window around the currently playing sample (16384 floats, centred on the
    /// playback position). The window itself stays the same size in every mode - it is what the
    /// samples visualization shows, and changing its length would change the shown time span.
    /// <para>
    /// The <see cref="SampleReadingStrategy.GlobalArray"/> variant returns a slice over the fully
    /// pre-read song array; the <see cref="SampleReadingStrategy.DirectRead"/> variant assembles the
    /// window in a reused scratch buffer that is only valid until the NEXT call of this method. All
    /// callers (FFT analysis and the samples visualization) consume the returned memory synchronously.
    /// </para>
    /// </summary>
    public async Task<ReadOnlyMemory<float>> GetCurrentlyPlayingSampleData()
    {
        if (playerDataProvider == null)
            return sampleZeroResult;

        int currentlyPlayingFrameStart = playerDataProvider!.Position - (FFT_BUFFER_32BIT_FLOAT_SIZE / 2);
        int currentlyPlayingFrameEnd = playerDataProvider!.Position + (FFT_BUFFER_32BIT_FLOAT_SIZE / 2);
        int currentlyPlayingFrameBufferZoneStart = currentlyPlayingFrameStart - SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE;
        int currentlyPlayingFrameBufferZoneEnd = currentlyPlayingFrameEnd + SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE;

        // Too early
        if (currentlyPlayingFrameStart <= 0)
            return sampleZeroResult;

        if (currentSampleReadingStrategy == SampleReadingStrategy.GlobalArray)
        {
            // Not enough data read yet
            if (globalSampleArrayWriteHead <= currentlyPlayingFrameEnd + 1)
                return sampleZeroResult;

            Memory<float> memorySlice = globalSampleArray.AsMemory(currentlyPlayingFrameStart, FFT_BUFFER_32BIT_FLOAT_SIZE);
            return memorySlice;
        }
        else if (currentSampleReadingStrategy == SampleReadingStrategy.DirectRead)
        {
            var sampleReader = sampleReaderDataProvider!;

            float[] window = directReadWindowScratch ??= new float[FFT_BUFFER_32BIT_FLOAT_SIZE];
            Array.Clear(window, 0, FFT_BUFFER_32BIT_FLOAT_SIZE);

            // Keep decoding where the previous call left off; only seek when the window left the zone
            // the decoder is positioned in (forward or backward jump).
            if (currentlyPlayingFrameBufferZoneEnd < sampleReader.Position || currentlyPlayingFrameBufferZoneStart > sampleReader.Position)
            {
                sampleReader.Seek(currentlyPlayingFrameBufferZoneStart);
            }

            // Drop the chunks that scrolled out of the read zone and give their buffers back to the pool.
            for (int i = directReadStrategyReadBuffers.Count - 1; i >= 0; i--)
            {
                if (directReadStrategyReadBuffers[i].framePosition < currentlyPlayingFrameBufferZoneStart)
                {
                    arrayPool.Return(directReadStrategyReadBuffers[i].frameData);
                    directReadStrategyReadBuffers.RemoveAt(i);
                }
            }

            // Decode forward until the end of the read window is covered. Each chunk's buffer is kept
            // in the list (pooled, no per-chunk copy or allocation), so the part of the window that
            // overlaps the previous one is not decoded again next frame.
            float[] sampleBuffer = arrayPool.Rent(SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE);
            try
            {
                int framesRead;
                while (sampleReader.Position < currentlyPlayingFrameEnd &&
                    (framesRead = sampleReader.ReadBytes(sampleBuffer.AsSpan(0, SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE))) > 0)
                {
                    // The buffer that was just filled belongs to this chunk - rent the next one up
                    // front so the filled buffer can stay in the list instead of being copied.
                    float[] filledBuffer = sampleBuffer;
                    sampleBuffer = arrayPool.Rent(SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE);

                    int position = sampleReader.Position - framesRead;
                    if (position >= currentlyPlayingFrameBufferZoneStart)
                        directReadStrategyReadBuffers.Add((position, filledBuffer, framesRead));
                    else
                        arrayPool.Return(filledBuffer); // chunk starts below the zone: never needed
                }
            }
            finally
            {
                arrayPool.Return(sampleBuffer);
            }

            // Assemble the window from the kept chunks.
            foreach (var (framePosition, frameData, validLength) in directReadStrategyReadBuffers)
            {
                int bufferStart = framePosition;
                int bufferEnd = framePosition + validLength;

                if (bufferEnd < currentlyPlayingFrameStart || bufferStart > currentlyPlayingFrameEnd)
                    continue;

                int copyStart = Math.Max(bufferStart, currentlyPlayingFrameStart);
                int copyEnd = Math.Min(bufferEnd, currentlyPlayingFrameEnd);

                int sourceIndex = copyStart - bufferStart;
                int destinationIndex = copyStart - currentlyPlayingFrameStart;
                int lengthToCopy = copyEnd - copyStart;

                Array.Copy(frameData, sourceIndex, window, destinationIndex, lengthToCopy);
            }

            return window;
        }
        else
        {
            throw new InvalidOperationException($"Unknown {nameof(SampleReadingStrategy)}: {currentSampleReadingStrategy}");
        }
    }

    /// <summary>
    /// Returns all pooled DirectRead chunk buffers to the pool (song or strategy switch).
    /// </summary>
    void ReleaseDirectReadBuffers()
    {
        foreach (var (_, frameData, _) in directReadStrategyReadBuffers)
            arrayPool.Return(frameData);
        directReadStrategyReadBuffers.Clear();
    }
    /// <summary>
    /// FFT size the frequency analysis runs at. In low power mode the analysis resolution drops to
    /// <see cref="FFT_BUFFER_LOW_POWER_SIZE"/>, otherwise the full <see cref="FFT_BUFFER_32BIT_FLOAT_SIZE"/>
    /// resolution is kept.
    /// </summary>
    int GetCurrentFftAnalysisSize() => Config.Data.LowPowerMode ? FFT_BUFFER_LOW_POWER_SIZE : FFT_BUFFER_32BIT_FLOAT_SIZE;

    /// <summary>
    /// Returns the <see cref="SpectrumAnalyzer"/> used by the FFT visualization. It is (re)created when
    /// the song's audio format changed (new song) or when the analysis resolution changed (low power
    /// mode toggled), so the analyzer always matches the current audio format and FFT size.
    /// </summary>
    SpectrumAnalyzer EnsureSpectrumAnalyzer(bool forceRecreate = false)
    {
        int requiredFftSize = GetCurrentFftAnalysisSize();
        if (forceRecreate || spectrumAnalyzer == null || spectrumAnalyzerFftSize != requiredFftSize)
        {
            spectrumAnalyzer = new SpectrumAnalyzer(GetCurrentAudioFormat(), requiredFftSize);
            spectrumAnalyzerFftSize = requiredFftSize;
        }
        return spectrumAnalyzer;
    }

    /// <summary>
    /// Runs the spectrum analysis for the current song and returns the resulting spectrum bins.
    /// The analysis resolution depends on the current mode (see <see cref="GetCurrentFftAnalysisSize"/>):
    /// in low power mode a smaller, centred slice of the read window is analyzed, so the frequency
    /// resolution drops while the read window itself (and with it the time span the samples
    /// visualization shows around the playback position) stays unchanged.
    /// <para>
    /// The returned array is the analyzer's internal, reused spectrum buffer: the next analysis
    /// overwrites it. Callers must consume it synchronously and must not retain or mutate it in a way
    /// that is expected to survive the next analysis call.
    /// </para>
    /// </summary>
    public async Task<float[]> GetCurrentFftSpectrumData(float[]? factorArray = null)
    {
        ReadOnlyMemory<float> sampleBufferMemory = await GetCurrentlyPlayingSampleData();

        // Low power mode lowers the FFT analysis resolution: the analyzer runs at a smaller FFT size
        // on a centred slice of the read window. The read window itself (GetCurrentlyPlayingSampleData)
        // keeps its full size, so the time span shown around the currently playing sample stays exactly
        // the same - only the frequency resolution of the analysis shrinks.
        SpectrumAnalyzer spectrumAnalyzer = EnsureSpectrumAnalyzer();
        int fftSize = spectrumAnalyzerFftSize;
        int channels = playerDataProvider?.FormatInfo?.ChannelCount ?? 2;
        int windowStart = Math.Max(0, (sampleBufferMemory.Length - fftSize) / 2);
        int windowLength = Math.Min(fftSize, sampleBufferMemory.Length - windowStart);

        if (factorArray == null)
        {
            spectrumAnalyzer.Process(sampleBufferMemory.Span.Slice(windowStart, windowLength), channels);
        }
        else
        {
            float[] workingArray = arrayPool.Rent(windowLength);
            Span<float> workingSpan = workingArray.AsSpan(0, windowLength);
            sampleBufferMemory.Span.Slice(windowStart, windowLength).CopyTo(workingSpan);

            for (int i = 0; i < windowLength; i++)
            {
                // A factor array sized like the read window is aligned to the same centred slice;
                // a factor array sized like the analysis window itself applies index by index.
                workingSpan[i] *= factorArray.Length == sampleBufferMemory.Length ? factorArray[windowStart + i] : factorArray[i];
            }

            spectrumAnalyzer.Process(workingSpan, channels);
            arrayPool.Return(workingArray);
        }

        // No per-call allocation: the analyzer fully rewrites its spectrum buffer on every Process,
        // so the caller may safely read (and scale) it in place until the next analysis.
        return spectrumAnalyzer.SpectrumData;
    }

    public IReadOnlyList<float>? GetCurrentSongEntireSampleData() => globalSampleArray == null || globalSampleArrayWriteHead < globalSampleArray.Length - SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE ? null : Array.AsReadOnly(globalSampleArray);
}