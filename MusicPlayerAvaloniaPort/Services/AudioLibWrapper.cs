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
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort;

public class AudioLibWrapper
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
    StreamDataProvider? analyzeDataProvider = null;
    readonly ArrayPool<float> analyzeSamplePool = ArrayPool<float>.Shared;
    const int ANALYZE_BUFFER_SIZE = 16384;
    SpectrumAnalyzer spectrumAnalyzer = new SpectrumAnalyzer(PlaybackDeviceFormat, ANALYZE_BUFFER_SIZE);

    public AudioLibWrapper()
    {
        if (Engine.PlaybackDevices.Length == 0)
        {
            throw new InvalidOperationException("No default playback device found.");
        }
        playbackDeviceInfo = Engine.PlaybackDevices.FirstOrDefault(d => d.IsDefault);
        playbackDevice = Engine.InitializePlaybackDevice(playbackDeviceInfo, PlaybackDeviceFormat, DeviceConfig);
        playbackDevice.Start();
    }

    public void PlaySong(string songPath)
    {
        playerDataProvider?.Dispose();
        playerDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });
        analyzeDataProvider?.Dispose();
        analyzeDataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });

        if (soundPlayer != null)
        {
            playbackDevice.MasterMixer.RemoveComponent(soundPlayer);

            soundPlayer.Dispose();
        }
        soundPlayer = new SoundPlayer(Engine, PlaybackDeviceFormat, playerDataProvider);
        playbackDevice.MasterMixer.AddComponent(soundPlayer);
        soundPlayer.Play();
    }

    public float[] GetCurrentFftSpectrumData()
    {
        analyzeDataProvider?.Seek(playerDataProvider!.Position); // Sync positions to get the same audio data for analysis

        var sampleBuffer = analyzeSamplePool.Rent(ANALYZE_BUFFER_SIZE);
        var sampleBufferSpan = sampleBuffer.AsSpan();

        _ = analyzeDataProvider!.ReadBytes(sampleBufferSpan);
        spectrumAnalyzer.Process(sampleBufferSpan, PlaybackDeviceFormat.Channels);
        var re = spectrumAnalyzer.SpectrumData.ToArray();

        analyzeSamplePool.Return(sampleBuffer);
        return re;
    }
}