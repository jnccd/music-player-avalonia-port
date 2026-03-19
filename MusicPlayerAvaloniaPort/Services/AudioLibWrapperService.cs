using Avalonia;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
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

namespace MusicPlayerAvaloniaPort.Services;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(AudioLibWrapperService))]
public class AudioLibWrapperService
{
    private static readonly AudioEngine Engine = new MiniAudioEngine();
    private static readonly DeviceConfig DeviceConfig = new MiniAudioDeviceConfig
    {
        PeriodSizeInFrames = 960, // 10ms at 48kHz = 480 frames @ 2 channels = 960 frames
        Playback = new DeviceSubConfig
        {
            ShareMode = ShareMode.Shared // Use shared mode for better compatibility with other applications
        },
        Capture = new DeviceSubConfig
        {
            ShareMode = ShareMode.Shared // Use shared mode for better compatibility with other applications
        },
        Wasapi = new WasapiSettings
        {
            Usage = WasapiUsage.ProAudio // Use ProAudio mode for lower latency on Windows
        }
    };
    private static readonly AudioFormat PlaybackDeviceFormat = AudioFormat.DvdHq;
    DeviceInfo playbackDeviceInfo;
    AudioPlaybackDevice playbackDevice;
    SoundPlayer? soundPlayer = null;
    StreamDataProvider? playerDataProvider = null;
    readonly ArrayPool<float> arrayPool = ArrayPool<float>.Shared;

    // Sample Reader Thread
    const int SAMPLE_BUFFER_SIZE = 1024;
    StreamDataProvider? sampleReaderDataProvider = null;
    float[]? globalSampleArray = null;
    int globalSampleArrayWriteHead = 0;
    Task? SampleReaderThread = null;
    bool CancelReading = false;

    // FFT Vars
    const int FFT_BUFFER_SIZE = 16384 / 4;
    SpectrumAnalyzer spectrumAnalyzer = new SpectrumAnalyzer(PlaybackDeviceFormat, FFT_BUFFER_SIZE);
    float[] fftZeroResult;

    // Setters
    /// <summary>
    /// [0,1]
    /// </summary>
    public float Volume
    {
        get => soundPlayer?.Volume ?? 0;
        set => soundPlayer?.Volume = value;
    }
    /// <summary>
    /// [0,1]
    /// </summary>
    public float? PlayProgress
    {
        get => soundPlayer?.Time / soundPlayer?.Duration;
        set
        {
            if (value != null)
                soundPlayer?.Seek(value.Value * soundPlayer.Duration);
        }
    }
    public PlaybackState? PlayState
    {
        get => soundPlayer?.State;
    }

    public AudioLibWrapperService()
    {
        fftZeroResult = arrayPool.Rent(FFT_BUFFER_SIZE);

        if (Engine.PlaybackDevices.Length == 0)
        {
            throw new InvalidOperationException("No default playback device found.");
        }
        playbackDeviceInfo = Engine.PlaybackDevices.FirstOrDefault(d => d.IsDefault);
        playbackDevice = Engine.InitializePlaybackDevice(playbackDeviceInfo, PlaybackDeviceFormat, DeviceConfig);
        playbackDevice.Start();
    }

    public void TogglePlayPause()
    {
        if (soundPlayer != null)
        {
            if (soundPlayer.State == PlaybackState.Playing)
                soundPlayer.Pause();
            else
                soundPlayer.Play();
        }
    }

    public void PlaySong(string songPath)
    {
        playerDataProvider?.Dispose();
        playerDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });
        sampleReaderDataProvider?.Dispose();
        sampleReaderDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });

        if (soundPlayer != null)
        {
            playbackDevice.MasterMixer.RemoveComponent(soundPlayer);
            soundPlayer.Dispose();
        }
        soundPlayer = new SoundPlayer(Engine, PlaybackDeviceFormat, playerDataProvider);
        playbackDevice.MasterMixer.AddComponent(soundPlayer);
        soundPlayer.Play();

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
            globalSampleArray = arrayPool.Rent(playerDataProvider.Length > 0 ? playerDataProvider.Length / 4 : 48000 * 60 * 5);
            globalSampleArrayWriteHead = 0;

            var sampleBuffer = arrayPool.Rent(SAMPLE_BUFFER_SIZE);
            var sampleBufferSpan = sampleBuffer.AsSpan();
            int bytesRead;

            while (!CancelReading &&
                globalSampleArrayWriteHead + SAMPLE_BUFFER_SIZE < globalSampleArray.Length &&
                // Read buffer from audio file
                (bytesRead = sampleReaderDataProvider!.ReadBytes(sampleBufferSpan)) > 0)
            {
                // Write into global array
                Buffer.BlockCopy(sampleBuffer, 0, globalSampleArray, globalSampleArrayWriteHead * sizeof(float), bytesRead / sizeof(float) * sizeof(float));
                globalSampleArrayWriteHead += bytesRead / sizeof(float);
            }
            Debug.WriteLine($"{DateTime.Now:HH:mm:ss.ffff} Done Reading!");

            arrayPool.Return(sampleBuffer);
        });
    }

    public float[] GetCurrentFftSpectrumData()
    {
        if (globalSampleArrayWriteHead <= (playerDataProvider!.Position / 4) + (FFT_BUFFER_SIZE / 2) + 1
            || playerDataProvider!.Position / 4 <= FFT_BUFFER_SIZE / 2 + 1)
            return fftZeroResult;

        Memory<float> memorySlice = globalSampleArray.AsMemory((playerDataProvider!.Position / 4) - (FFT_BUFFER_SIZE / 2), FFT_BUFFER_SIZE);
        Span<float> sampleBufferSpan = memorySlice.Span;

        spectrumAnalyzer.Process(sampleBufferSpan, PlaybackDeviceFormat.Channels);
        var re = spectrumAnalyzer.SpectrumData.ToArray();

        return re;
    }
}