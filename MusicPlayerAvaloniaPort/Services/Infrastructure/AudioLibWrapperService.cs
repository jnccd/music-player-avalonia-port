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
    List<(int framePosition, float[] frameData)> directReadStrategyReadBuffers = [];

    // FFT Vars
    public const int FFT_BUFFER_32BIT_FLOAT_SIZE = 16384;
    private static readonly AudioFormat AnalyzeFormat = AudioFormat.Studio;
    SpectrumAnalyzer spectrumAnalyzer = new(AnalyzeFormat, FFT_BUFFER_32BIT_FLOAT_SIZE);
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
        spectrumAnalyzer = new SpectrumAnalyzer(GetCurrentAudioFormat(), FFT_BUFFER_32BIT_FLOAT_SIZE);

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
            directReadStrategyReadBuffers.Clear();
        }
        if (currentSampleReadingStrategy == SampleReadingStrategy.GlobalArray)
            SampleReaderThread = Task.Run(() =>
            {
                globalSampleArrayWriteHead = 0;
                int requiredGlobalSampleArrayLength = playerDataProvider.Length > 0 ? playerDataProvider.Length : 48000 * 60 * 5;
                globalSampleArray = new float[requiredGlobalSampleArrayLength];
                directReadStrategyReadBuffers.Clear();
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
            float[] returnArray = new float[FFT_BUFFER_32BIT_FLOAT_SIZE];

            if (currentlyPlayingFrameBufferZoneEnd < sampleReaderDataProvider!.Position || currentlyPlayingFrameBufferZoneStart > sampleReaderDataProvider.Position)
            {
                sampleReaderDataProvider.Seek(currentlyPlayingFrameBufferZoneStart);
            }

            directReadStrategyReadBuffers.RemoveAll(x => x.framePosition < currentlyPlayingFrameBufferZoneStart);

            var sampleBuffer = arrayPool.Rent(SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE);
            var sampleBufferSpan = sampleBuffer.AsSpan();
            int framesRead;

            while (sampleReaderDataProvider!.Position < currentlyPlayingFrameEnd &&
                    (framesRead = sampleReaderDataProvider!.ReadBytes(sampleBufferSpan)) > 0)
            {
                var position = sampleReaderDataProvider.Position - framesRead;
                if (position >= currentlyPlayingFrameBufferZoneStart)
                    directReadStrategyReadBuffers.Add((position, sampleBufferSpan.ToArray()));
            }

            foreach (var (framePosition, frameData) in directReadStrategyReadBuffers)
            {
                int bufferStart = framePosition;
                int bufferEnd = framePosition + frameData.Length;

                if (bufferEnd < currentlyPlayingFrameStart || bufferStart > currentlyPlayingFrameEnd)
                    continue;

                int copyStart = Math.Max(bufferStart, currentlyPlayingFrameStart);
                int copyEnd = Math.Min(bufferEnd, currentlyPlayingFrameEnd);

                int sourceIndex = copyStart - bufferStart;
                int destinationIndex = copyStart - currentlyPlayingFrameStart;
                int lengthToCopy = copyEnd - copyStart;

                Array.Copy(frameData, sourceIndex, returnArray, destinationIndex, lengthToCopy);
            }

            return returnArray;
        }
        else
        {
            throw new InvalidOperationException($"Unknown {nameof(SampleReadingStrategy)}: {currentSampleReadingStrategy}");
        }
    }
    public async Task<float[]> GetCurrentFftSpectrumData(float[]? factorArray = null)
    {
        ReadOnlyMemory<float> sampleBufferMemory = await GetCurrentlyPlayingSampleData();

        if (factorArray == null)
        {
            spectrumAnalyzer.Process(sampleBufferMemory.Span, playerDataProvider?.FormatInfo?.ChannelCount ?? 2);
        }
        else
        {
            float[] workingArray = arrayPool.Rent(FFT_BUFFER_32BIT_FLOAT_SIZE);
            Span<float> workingSpan = workingArray;
            sampleBufferMemory.Span.CopyTo(workingSpan);

            for (int i = 0; i < FFT_BUFFER_32BIT_FLOAT_SIZE; i++)
            {
                workingSpan[i] *= factorArray[i];
            }

            spectrumAnalyzer.Process(workingSpan, playerDataProvider?.FormatInfo?.ChannelCount ?? 2);
            arrayPool.Return(workingArray);
        }

        var re = spectrumAnalyzer.SpectrumData.ToArray();

        return re;
    }

    public IReadOnlyList<float>? GetCurrentSongEntireSampleData() => globalSampleArray == null || globalSampleArrayWriteHead < globalSampleArray.Length - SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE ? null : Array.AsReadOnly(globalSampleArray);
}