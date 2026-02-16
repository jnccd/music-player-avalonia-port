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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort;

public class AudioLibWrapper
{
    private static readonly AudioFormat Format = AudioFormat.DvdHq;
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
    DeviceInfo playbackDeviceInfo;
    AudioPlaybackDevice playbackDevice;
    StreamDataProvider? dataProvider = null;
    SoundPlayer? soundPlayer = null;

    public AudioLibWrapper()
    {
        if (Engine.PlaybackDevices.Length == 0)
        {
            throw new InvalidOperationException("No default playback device found.");
        }
        playbackDeviceInfo = Engine.PlaybackDevices.FirstOrDefault(d => d.IsDefault);
        playbackDevice = Engine.InitializePlaybackDevice(playbackDeviceInfo, Format, DeviceConfig);
        playbackDevice.Start();
    }

    public void PlaySong(string songPath)
    {
        dataProvider?.Dispose();
        dataProvider = new StreamDataProvider(Engine, new FileStream(songPath, FileMode.Open, FileAccess.Read), new ReadOptions { ReadTags = false });

        if (soundPlayer != null)
        {
            playbackDevice.MasterMixer.RemoveComponent(soundPlayer);

            soundPlayer.Dispose();
        }
        soundPlayer = new SoundPlayer(Engine, Format, dataProvider);
        playbackDevice.MasterMixer.AddComponent(soundPlayer);
        soundPlayer.Play();
    }
}