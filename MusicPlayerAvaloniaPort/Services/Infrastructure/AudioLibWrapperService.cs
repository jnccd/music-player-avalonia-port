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

    // Sample Reader Thread
    const int SAMPLE_READER_BUFFER_32BIT_FLOAT_SIZE = 4096;
    StreamDataProvider? sampleReaderDataProvider = null;
    float[]? globalSampleArray = null;
    int globalSampleArrayWriteHead = 0;
    Task? SampleReaderThread = null;
    bool CancelReading = false;
    const int SAMPLE_OUTPUT_BUFFER_32BIT_FLOAT_SIZE = 16384;

    // FFT Vars
    public const int FFT_BUFFER_32BIT_FLOAT_SIZE = 16384;
    private static readonly AudioFormat AnalyzeFormat = AudioFormat.Studio;
    SpectrumAnalyzer spectrumAnalyzer = new SpectrumAnalyzer(AnalyzeFormat, FFT_BUFFER_32BIT_FLOAT_SIZE);
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
            throw new Exception("Sound Player gon");

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

    public void PlaySong(string songPath)
    {
        playerDataProvider?.Dispose();
        playerDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });
        sampleReaderDataProvider?.Dispose();
        sampleReaderDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });
        spectrumAnalyzer = new SpectrumAnalyzer(GetCurrentAudioFormat(), FFT_BUFFER_32BIT_FLOAT_SIZE);

        if (soundPlayer != null)
        {
            Task.Run(() =>
            {
                playbackDevice.MasterMixer.RemoveComponent(soundPlayer);
                soundPlayer.Dispose();
                playbackDevice.Dispose();
            });
        }

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
        SampleReaderThread = Task.Run(() =>
        {
            if (globalSampleArray != null) arrayPool.Return(globalSampleArray);
            globalSampleArray = arrayPool.Rent(playerDataProvider.Length > 0 ? playerDataProvider.Length : 48000 * 60 * 5);
            globalSampleArrayWriteHead = 0;

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

    public ReadOnlySpan<float> GetCurrentSampleData()
    {
        if (globalSampleArrayWriteHead <= playerDataProvider!.Position + (FFT_BUFFER_32BIT_FLOAT_SIZE / 2) + 1
            || playerDataProvider!.Position <= FFT_BUFFER_32BIT_FLOAT_SIZE / 2 + 1)
            return sampleZeroResult;

        Memory<float> memorySlice = globalSampleArray.AsMemory(playerDataProvider!.Position - (FFT_BUFFER_32BIT_FLOAT_SIZE / 2), FFT_BUFFER_32BIT_FLOAT_SIZE);
        Span<float> sampleBufferSpan = memorySlice.Span;

        return sampleBufferSpan;
    }
    public float[] GetCurrentFftSpectrumData(float[]? factorArray = null)
    {
        ReadOnlySpan<float> sampleBufferSpan = GetCurrentSampleData();

        if (factorArray == null)
        {
            spectrumAnalyzer.Process(sampleBufferSpan, AnalyzeFormat.Channels);
        }
        else
        {
            float[] workingArray = arrayPool.Rent(FFT_BUFFER_32BIT_FLOAT_SIZE);
            Span<float> workingSpan = workingArray;
            sampleBufferSpan.CopyTo(workingSpan);

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

    public IReadOnlyList<float>? GetCurrentSongSampleData() => globalSampleArray == null ? null : Array.AsReadOnly(globalSampleArray);
}