using MusicPlayerAvaloniaPort.Persistence.Configuration;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;
using System;
using System.Diagnostics;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

/// <summary>
/// Captures the audio the whole operating system outputs - every application, not just this player - so
/// the FFT diagram can visualize the system audio instead of the currently playing song file. It backs
/// the "Visualize System Audio" option of the options view (persisted as
/// <see cref="ConfigData.CaptureSystemAudio"/>).
/// <para>
/// The device is created on the engine of <see cref="AudioLibWrapperService"/>, so capture and playback
/// share one audio context. Which device that is depends on the platform:
/// </para>
/// <list type="bullet">
/// <item>Windows/WASAPI has a real loopback mode: the default output device is opened as a capture
/// device, which is exactly the full system output mix.</item>
/// <item>Linux/macOS have no loopback API. There a capture device that mirrors an output (a
/// PulseAudio/PipeWire <c>monitor</c> source, or a virtual driver like BlackHole) is used instead, and
/// when none exists the option fails with an explanation in <see cref="State"/> instead of silently
/// showing nothing.</item>
/// </list>
/// <para>
/// The captured samples are kept in a ring buffer that always holds the newest
/// <see cref="AudioLibWrapperService.FFT_BUFFER_32BIT_FLOAT_SIZE"/> interleaved samples, so the diagram
/// can read the most recent audio without ever blocking the capture thread (which runs on the audio
/// backend's real-time thread).
/// </para>
/// </summary>
[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SystemAudioCaptureService))]
public class SystemAudioCaptureService
{
    /// <summary>
    /// Capacity of the capture ring buffer in interleaved samples: the largest analysis/read window any
    /// visualization mode asks for (see <see cref="AudioLibWrapperService.FFT_BUFFER_32BIT_FLOAT_SIZE"/>).
    /// </summary>
    const int CAPTURE_RING_CAPACITY = AudioLibWrapperService.FFT_BUFFER_32BIT_FLOAT_SIZE;

    /// <summary>
    /// Format the capture device is opened with. F32 stereo interleaved is what the rest of the player
    /// works with internally (the FFT analysis and every visualization mode consume interleaved 32 bit
    /// floats) and the WASAPI loopback path delivers natively; the other backends convert to it.
    /// </summary>
    static readonly AudioFormat CAPTURE_FORMAT = new()
    {
        Format = SampleFormat.F32,
        Channels = 2,
        Layout = ChannelLayout.Stereo,
        SampleRate = 48000
    };

    /// <summary>
    /// Case insensitive name markers of capture devices that mirror the system output on platforms
    /// without a real loopback mode (Linux PulseAudio/PipeWire monitor sources, the usual virtual loopback
    /// drivers on macOS, and the "Stereo Mix" style inputs of Windows sound cards).
    /// </summary>
    static readonly string[] systemOutputCaptureDeviceNameMarkers =
    [
        "monitor",      // PulseAudio / PipeWire sink monitor ("Monitor of ...", "alsa_output...monitor")
        "stereo mix",   // Windows sound card loopback input
        "what u hear",  // Windows (older Creative drivers)
        "blackhole",    // macOS virtual loopback driver
        "soundflower",  // macOS virtual loopback driver
        "loopback",     // e.g. "Loopback Audio" (macOS/Windows)
        "vb-cable",     // Windows virtual audio cable
    ];

    readonly AudioLibWrapperService audioLibWrapperService;

    // Loudness normalization of the captured stream.
    //
    // The diagram divides its bars by the loudness of the source it draws: for a song that is the RMS of
    // the whole song (measured once and stored per song, see SongVolumeService), and the diagram's
    // absolute scaling constants are calibrated against that divisor - a signal drawn at its own RMS fills
    // the diagram (a song with the typical RMS of 0.12 measures ~130% of the height, i.e. it clamps at the
    // top, see DiagramDataMapperService.EnsureBinScaleFactors). A live stream has no "whole song" to
    // measure, so the divisor is a smoothed RMS of the recent capture instead.
    //
    // This matters a lot in practice: the loopback signal is the operating system's output mix, i.e. it has
    // already been scaled by the application and endpoint volume. Measured on a normal Windows setup it
    // arrives about 28 dB below the player's own output level, so drawing it with a divisor of 1 (as the
    // first version of this feature did) left the diagram a few tenths of a percent tall. Dividing by the
    // measured RMS puts the capture on exactly the same scale as a song.
    /// <summary>
    /// Divisor applied to every diagram column while the system audio is visualized (see
    /// <see cref="DiagramDataMapperService"/>). It mirrors the per-song divisor of the song path, so both
    /// sources draw at a comparable height regardless of the operating system's output volume.
    /// </summary>
    public float VolumeDivisor => Math.Max(smoothedRms, MIN_VOLUME_DIVISOR);

    /// <summary>
    /// Smallest divisor the normalization ever uses, i.e. the loudest the diagram can be boosted. It caps
    /// the boost at roughly 55 dB below the full-scale reference so that a near-silent capture (device
    /// noise, a dither-only stream) is not amplified into a full-height noise band.
    /// </summary>
    const float MIN_VOLUME_DIVISOR = 0.002f;
    /// <summary>
    /// Signals quieter than this carry no loudness information (they are inaudible at about -80 dBFS) and
    /// are ignored by the estimate: holding the last value keeps the divisor from collapsing towards the
    /// floor while nothing is playing.
    /// </summary>
    const float RMS_SILENCE_THRESHOLD = 1e-4f;
    /// <summary>
    /// Time constant of the estimate while the level is rising. Deliberately slower than a beat: the
    /// divisor has to follow the programme level (app/endpoint volume changes, a loud album after a quiet
    /// one), not single transients, or the whole diagram would visibly pump down on every drum hit. Within
    /// the song path this cannot happen at all - there the divisor is the RMS of the whole song.
    /// </summary>
    const float RMS_ATTACK_SECONDS = 1f;
    /// <summary>Time constant while the level is falling, again slow so a quiet passage does not blow the diagram up.</summary>
    const float RMS_RELEASE_SECONDS = 4f;

    /// <summary>Smoothed RMS of the captured stream (0 until the first samples arrived).</summary>
    float smoothedRms;
    long lastRmsUpdateTimestamp;

    // Newest captured samples (interleaved): written by the audio backend's capture thread, read by the
    // diagram. Both sides only touch it for the duration of one copy, so a plain lock is enough and the
    // capture thread never has to allocate or wait for anything slow.
    readonly float[] ringBuffer = new float[CAPTURE_RING_CAPACITY];
    readonly object ringLock = new();
    int ringWriteHead;
    long capturedSampleCount;

    // The device itself (created/disposed by the options toggle and at startup).
    readonly object deviceLock = new();
    AudioCaptureDevice? captureDevice;

    /// <summary>
    /// True while the user wants the system audio visualized. It mirrors the persisted option, except
    /// that a failed start clears it again (so an unsupported platform is not retried on every start).
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>True while captured samples are actually arriving (the device is up and running).</summary>
    public bool IsCapturing
    {
        get { lock (deviceLock) return captureDevice is { IsRunning: true }; }
    }

    /// <summary>
    /// Human readable state of the capture (running device, stopped, or why it could not be started).
    /// Shown by the options view and mirrored into the app's error log when a start fails.
    /// </summary>
    public string State { get; private set; } = "System audio capture is off.";

    /// <summary>
    /// Raised whenever <see cref="State"/>/<see cref="IsCapturing"/> changed - i.e. when the option was
    /// toggled or a start failed. The main view uses it to repaint the diagram (which animates for the
    /// captured audio even while no song plays).
    /// </summary>
    public event Action? StateChanged;

    public SystemAudioCaptureService(AudioLibWrapperService audioLibWrapperService)
    {
        this.audioLibWrapperService = audioLibWrapperService;
    }

    /// <summary>
    /// Starts the capture when the persisted option is on. Called once at startup (after the audio engine
    /// exists), so an enabled option survives an app restart.
    /// </summary>
    public void ApplyConfiguredSetting()
    {
        if (Config.Data.CaptureSystemAudio)
            StartCapture();
    }

    /// <summary>
    /// Turns the capture on or off and persists the setting. Returns the state that actually resulted:
    /// a request to start can fail (unsupported platform, no loopback device, device in use), in which
    /// case the option is stored as off and <see cref="State"/> explains why.
    /// </summary>
    public bool SetEnabled(bool enabled)
    {
        if (enabled)
            StartCapture();
        else
            StopCapture();

        Config.Data.CaptureSystemAudio = IsEnabled;
        Config.Save();
        return IsEnabled;
    }

    /// <summary>
    /// Copies the newest captured samples into <paramref name="destination"/>. When less has been
    /// captured than the destination holds (including nothing at all, e.g. right after enabling the
    /// option), the start of the destination is filled with silence, so the caller always gets a full,
    /// usable analysis window. Returns false when no sample was captured yet.
    /// <para>
    /// Reading also feeds the loudness estimate behind <see cref="VolumeDivisor"/> (over the newly copied
    /// samples only), so the diagram's normalization follows the stream without a second pass over it.
    /// </para>
    /// </summary>
    public bool TryReadNewestSamples(Span<float> destination)
    {
        if (destination.Length == 0)
            return false;

        lock (ringLock)
        {
            int validSampleCount = (int)Math.Min(capturedSampleCount, ringBuffer.Length);
            int copyCount = Math.Min(validSampleCount, destination.Length);

            int silenceCount = destination.Length - copyCount;
            if (silenceCount > 0)
                destination.Slice(0, silenceCount).Clear();

            if (copyCount > 0)
            {
                // The newest sample sits right before the write head; walk back copyCount samples
                // (wrapping around the ring) and copy them front to back.
                int readStart = ringWriteHead - copyCount;
                if (readStart < 0)
                    readStart += ringBuffer.Length;

                int firstPart = Math.Min(copyCount, ringBuffer.Length - readStart);
                ringBuffer.AsSpan(readStart, firstPart).CopyTo(destination.Slice(silenceCount, firstPart));
                if (firstPart < copyCount)
                    ringBuffer.AsSpan(0, copyCount - firstPart).CopyTo(destination.Slice(silenceCount + firstPart));

                UpdateLoudnessEstimate(destination.Slice(silenceCount, copyCount));
            }

            return validSampleCount > 0;
        }
    }

    /// <summary>
    /// Feeds captured samples into the smoothed RMS behind <see cref="VolumeDivisor"/>: fast attack (a
    /// louder passage lowers the bars quickly) and slow release (a quiet passage does not raise them again
    /// immediately), both as real time constants, so the estimate does not depend on the diagram's frame
    /// rate. Callers hold <see cref="ringLock"/>.
    /// </summary>
    void UpdateLoudnessEstimate(ReadOnlySpan<float> samples)
    {
        double sumOfSquares = 0;
        for (int i = 0; i < samples.Length; i++)
            sumOfSquares += (double)samples[i] * samples[i];

        float windowRms = (float)Math.Sqrt(sumOfSquares / samples.Length);

        if (windowRms < RMS_SILENCE_THRESHOLD)
            return; // nothing playing (or only dither): keep the last estimate

        long now = Environment.TickCount64;
        if (lastRmsUpdateTimestamp == 0 || smoothedRms <= 0f)
        {
            // First real measurement: adopt it instead of ramping up from zero, so the first frames after
            // enabling the option are already scaled correctly.
            smoothedRms = windowRms;
            lastRmsUpdateTimestamp = now;
            return;
        }

        float elapsedSeconds = Math.Max(0f, (now - lastRmsUpdateTimestamp) / 1000f);
        lastRmsUpdateTimestamp = now;

        float timeConstant = windowRms > smoothedRms ? RMS_ATTACK_SECONDS : RMS_RELEASE_SECONDS;
        float coefficient = 1f - MathF.Exp(-elapsedSeconds / timeConstant);
        smoothedRms += (windowRms - smoothedRms) * coefficient;
    }

    void StartCapture()
    {
        // The state change is published after the lock was released, so an event handler never runs with
        // the device lock held (handlers read IsCapturing and may marshal to the UI thread).
        string newState;

        lock (deviceLock)
        {
            if (captureDevice is { IsRunning: true })
            {
                IsEnabled = true;
                newState = $"Capturing system audio output from \"{DescribeDevice(captureDevice)}\".";
            }
            else
            {
                try
                {
                    var device = CreateSystemOutputCaptureDevice();
                    ResetRingBuffer();
                    device.OnAudioProcessed += OnAudioProcessedCaptured;

                    // Assigned before Start, so a failing Start is disposed by the catch below (the device
                    // is already registered with the engine at this point).
                    captureDevice = device;
                    device.Start();

                    IsEnabled = true;
                    newState = $"Capturing system audio output from \"{DescribeDevice(device)}\".";
                    Debug.WriteLine($"{DateTime.Now:HH:mm:ss.ffff} System audio capture started ({DescribeDevice(device)}).");
                }
                catch (Exception ex)
                {
                    DisposeCaptureDevice();
                    IsEnabled = false;
                    newState = $"System audio capture is unavailable: {ex.Message}";
                    Program.WriteErrorLog($"System audio capture could not be started: {ex}");
                }
            }
        }

        SetState(newState);
    }

    void StopCapture()
    {
        string newState;

        lock (deviceLock)
        {
            bool wasCapturing = captureDevice != null;
            DisposeCaptureDevice();
            ResetRingBuffer();
            IsEnabled = false;
            newState = wasCapturing ? "System audio capture stopped." : "System audio capture is off.";
        }

        SetState(newState);
        Debug.WriteLine($"{DateTime.Now:HH:mm:ss.ffff} System audio capture stopped.");
    }

    /// <summary>
    /// Creates the capture device that delivers the system audio output: the WASAPI loopback device on
    /// Windows, otherwise a capture device that mirrors an output (see
    /// <see cref="FindSystemOutputCaptureDevice"/>). Throws with an explanation when neither exists.
    /// </summary>
    AudioCaptureDevice CreateSystemOutputCaptureDevice()
    {
        var engine = audioLibWrapperService.AudioEngine;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return engine.InitializeLoopbackDevice(CAPTURE_FORMAT);
            }
            catch (Exception loopbackException)
            {
                // The WASAPI loopback path is the normal one; it can still fail (e.g. an engine that fell
                // back to DirectSound/WinMM). A "Stereo Mix" style recording device carries the same
                // signal, so try that before reporting the option as unavailable.
                var fallbackDevice = FindSystemOutputCaptureDevice(engine);
                if (fallbackDevice == null)
                    throw new NotSupportedException(
                        $"{loopbackException.Message} No \"Stereo Mix\" style recording device was found either.",
                        loopbackException);
                return engine.InitializeCaptureDevice(fallbackDevice, CAPTURE_FORMAT);
            }
        }

        var monitorDevice = FindSystemOutputCaptureDevice(engine);
        if (monitorDevice == null)
            throw new NotSupportedException(OperatingSystem.IsMacOS()
                ? "macOS does not offer system audio capture by itself - install a virtual loopback driver (e.g. BlackHole) and make it available as an input device."
                : "No capture device that mirrors the system output was found - Linux needs a PulseAudio/PipeWire monitor source of the default sink.");

        return engine.InitializeCaptureDevice(monitorDevice, CAPTURE_FORMAT);
    }

    /// <summary>
    /// Returns the first capture device whose name marks it as a mirror of an output
    /// (see <see cref="systemOutputCaptureDeviceNameMarkers"/>), or null when there is none.
    /// </summary>
    static DeviceInfo? FindSystemOutputCaptureDevice(AudioEngine engine)
    {
        // Re-enumerate first: the device list is cached, and an output device that appeared while the app
        // was already running has to be visible here.
        engine.UpdateAudioDevicesInfo();

        foreach (var deviceInfo in engine.CaptureDevices)
        {
            if (deviceInfo.Name == null)
                continue;

            if (systemOutputCaptureDeviceNameMarkers.Any(marker =>
                deviceInfo.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return deviceInfo;
            }
        }

        return null;
    }

    /// <summary>
    /// Capture callback (audio backend thread): appends the captured samples to the ring buffer. Runs for
    /// every captured chunk, so it must stay allocation free and must never throw.
    /// </summary>
    void OnAudioProcessedCaptured(Span<float> samples, Capability capability)
    {
        try
        {
            int sampleCount = samples.Length;
            if (sampleCount == 0)
                return;

            lock (ringLock)
            {
                if (sampleCount >= ringBuffer.Length)
                {
                    // More than the ring holds: only the newest window is of interest, and it lands at the
                    // start of the buffer, which is also where the next write continues.
                    samples.Slice(sampleCount - ringBuffer.Length).CopyTo(ringBuffer);
                    ringWriteHead = 0;
                }
                else
                {
                    int firstPart = Math.Min(sampleCount, ringBuffer.Length - ringWriteHead);
                    samples.Slice(0, firstPart).CopyTo(ringBuffer.AsSpan(ringWriteHead));
                    if (firstPart < sampleCount)
                        samples.Slice(firstPart).CopyTo(ringBuffer);

                    ringWriteHead = (ringWriteHead + sampleCount) % ringBuffer.Length;
                }

                capturedSampleCount += sampleCount;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{DateTime.Now:HH:mm:ss.ffff} System audio capture callback failed: {ex}");
        }
    }

    void ResetRingBuffer()
    {
        lock (ringLock)
        {
            Array.Clear(ringBuffer);
            ringWriteHead = 0;
            capturedSampleCount = 0;
            // The loudness estimate belongs to the captured stream: a new capture starts from scratch
            // instead of inheriting the level of the previous one.
            smoothedRms = 0f;
            lastRmsUpdateTimestamp = 0;
        }
    }

    /// <summary>Stops and disposes the capture device (no-op when there is none). Callers hold deviceLock.</summary>
    void DisposeCaptureDevice()
    {
        if (captureDevice == null)
            return;

        captureDevice.OnAudioProcessed -= OnAudioProcessedCaptured;
        try
        {
            captureDevice.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{DateTime.Now:HH:mm:ss.ffff} Stopping the system audio capture device failed: {ex}");
        }
        captureDevice.Dispose();
        captureDevice = null;
    }

    static string DescribeDevice(AudioDevice device) => device.Info?.Name ?? "default device";

    void SetState(string state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
