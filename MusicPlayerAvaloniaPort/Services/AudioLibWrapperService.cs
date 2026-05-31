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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Services;

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
    const int SAMPLE_BUFFER_SIZE = 1024;
    StreamDataProvider? sampleReaderDataProvider = null;
    float[]? globalSampleArray = null;
    int globalSampleArrayWriteHead = 0;
    Task? SampleReaderThread = null;
    bool CancelReading = false;

    // FFT Vars
    const int FFT_BUFFER_SIZE = 16384 / 4;
    private static readonly AudioFormat AnalyzeFormat = AudioFormat.Studio;
    SpectrumAnalyzer spectrumAnalyzer = new SpectrumAnalyzer(AnalyzeFormat, FFT_BUFFER_SIZE);
    float[] fftZeroResult;

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

    public AudioLibWrapperService()
    {
        fftZeroResult = arrayPool.Rent(FFT_BUFFER_SIZE);

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

    public void TogglePlayPause()
    {
        if (soundPlayer == null)
            throw new Exception("Sound Player gon");

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
        if (soundPlayer.State == PlaybackState.Playing)
            soundPlayer.Pause();
        else
            soundPlayer.Play();
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
            playbackDevice.Dispose();
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

        spectrumAnalyzer.Process(sampleBufferSpan, AnalyzeFormat.Channels);
        var re = spectrumAnalyzer.SpectrumData.ToArray();

        return re;
    }
}