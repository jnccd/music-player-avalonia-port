using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Services.Visualization;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(DiagramDataMapperService))]
public class DiagramDataMapperService
{
    readonly AudioLibWrapperService audioLibWrapperService;
    readonly SongPlaybackService songPlaybackService;
    readonly DbWrapperService dbWrapperService;
    readonly SongVolumeService songVolumeService;
    readonly SystemAudioCaptureService systemAudioCaptureService;

    public DiagramDataMapperService(AudioLibWrapperService audioLibWrapperService, SongPlaybackService songPlaybackService,
        DbWrapperService dbWrapperService, SongVolumeService songVolumeService, SystemAudioCaptureService systemAudioCaptureService)
    {
        this.audioLibWrapperService = audioLibWrapperService;
        this.songPlaybackService = songPlaybackService;
        this.dbWrapperService = dbWrapperService;
        this.songVolumeService = songVolumeService;
        this.systemAudioCaptureService = systemAudioCaptureService;

        // A song that was first played without a stored volume (GlobalArray read) gets its volume
        // measured and stored while it is still playing (see SongVolumeService). The cached divisor
        // below must notice that, or the bars would keep the unscaled height for the whole song.
        this.songVolumeService.VolumeDataChanged += OnVolumeDataChanged;
    }

    void OnVolumeDataChanged() => Interlocked.Increment(ref volumeCacheInvalidationVersion);

    // Fraction of the FFT bin axis skipped at the low-frequency start: ReadStart = binCount * this - 1.
    // 0 made the log curve ramp from the near-DC bins, which put a long, near-zero "dead shelf" at the
    // far left (the old client's first column sits at ~bin 2). The value below trims that shelf so the
    // low edge starts around bin 4 (~10 Hz) like the DXMG client; keep it as a knob for A/B.
    private const double FFT_WINDOW_PERCENT_CHOPPED_BEGINNING = 0.0006;
    private const double FFT_WINDOW_PERCENT_CHOPPED_END = 0.66;
    // Global scale applied to every FFT bin (with the per-bin sqrt(i+1) pre-emphasis) before the
    // per-column max is taken and the per-song volume divisor is applied. RAISED from 9001 because
    // the max-aggregation (DXMG-style) plus the new cutoff made the Avalonia bars read taller than
    // the old client; it is the primary overall-height knob.
    private const float FFT_WINDOW_VALUE_DIVISOR = 14000;
    // Accentuation of the human-voice / "talking" band on the log-frequency axis. The band between the
    // two centers keeps the plain log mapping; the space for it is taken from the sides, which are
    // configured independently (center = where that side's ramp starts/ends, width = how soft the ramp
    // is, strength = how hard that side is squeezed). A strength above 0 squeezes its side into fewer
    // columns, so that side's bars get thinner - LOW_STRENGTH > HIGH_STRENGTH presses the low-frequency
    // bars thinner than the high ones. Both strengths at 0 disable the accent entirely (the mapping is
    // then identical to a plain log mapping).
    // ~0.59 / ~0.88 are where 300 Hz / 3000 Hz sit on the axis, i.e. the talking range.
    private const double FFT_WINDOW_ACCENT_LOW_CENTER = 0.59;
    private const double FFT_WINDOW_ACCENT_LOW_WIDTH = 0.05;
    private const double FFT_WINDOW_ACCENT_LOW_STRENGTH = 1.0;
    private const double FFT_WINDOW_ACCENT_HIGH_CENTER = 0.88;
    private const double FFT_WINDOW_ACCENT_HIGH_WIDTH = 0.05;
    private const double FFT_WINDOW_ACCENT_HIGH_STRENGTH = 0.5;

    private const double FFT_SAMPLES_HAMMING_WINDOW_DOWNWARD_EXPONENT = 2;

    private float[]? smoothedData;
    private float[]? mappedData;

    // Analysis window of the system audio capture and the window the samples visualization draws while it
    // is active. Both are only valid until the next call of the method that filled them (same contract as
    // AudioLibWrapperService.GetCurrentlyPlayingSampleData), and they are reused so a frame allocates nothing.
    float[]? systemAudioAnalysisWindow;
    float[]? systemAudioSamplesWindow;

    // Reusable per-frame caches: the frequency bins of a fixed-length FFT and the fixed (bin count,
    // column count) pairing only change when the FFT resolution or the control width changes, so the
    // per-bin scale factors and the per-column bin ranges are computed once per such change instead
    // of doing width Math.Pow / bin Math.Sqrt calls on every frame.
    float[]? binScaleFactors;
    int binScaleFactorCount;
    int[]? columnBinRangeFrom;
    int[]? columnBinRangeTo;
    int columnBinRangeBinCount;
    int columnBinRangeColumnCount;
    // The volume divisor only changes together with the currently playing song (its DB row) or when
    // the stored volume of that row is (re)measured while it plays - both rare. It is therefore
    // looked up once per song / change instead of opening an EF context on every frame; as a fallback
    // for external writers (e.g. a sync pull) the row is also re-validated periodically.
    Guid? volumeCacheSongId;
    float volumeCacheDivisor = 1f;
    long volumeCacheLastQueryTimestamp;
    long volumeCacheVersionAtQuery;
    const long VolumeCacheMaxAgeMs = 5000;
    int volumeCacheInvalidationVersion; // bumped by OnVolumeDataChanged (Interlocked, cross-thread)

    private const float THETA = 3.0f;
    private GaussianCache gaussianCache = new GaussianCache(THETA);
    private float[] hammingWindowFactorArray = Enumerable
        .Range(0, AudioLibWrapperService.FFT_BUFFER_32BIT_FLOAT_SIZE)
        .Select(i => (float)Math.Pow(HammingWindowCache.ComputeHammingWindow(i, AudioLibWrapperService.FFT_BUFFER_32BIT_FLOAT_SIZE), FFT_SAMPLES_HAMMING_WINDOW_DOWNWARD_EXPONENT))
        .ToArray();

    /// <summary>
    /// Reach of the smoothing kernel as a fraction of the diagram width. The smoothing is deliberately
    /// sized relative to the width instead of in absolute columns, exactly like the old DXMG client:
    /// there the window (430 * UiScaling.scaleMult columns) and the kernel (6 * UiScaling.scaleMult
    /// samples, decaying with 2^(-j / scaleMult)) both scale together, so the kernel always covers
    /// 6/430 of the width. Avalonia's columns are device-independent pixels, so a fixed sample count
    /// would shrink relative to the diagram on wider windows or higher display scaling; deriving it
    /// from the width keeps the smoothing identical in every case.
    /// </summary>
    private const double SMOOTHING_KERNEL_REACH_FRACTION = 6.0 / 430.0;

    // Decay factors 2^(-j / decayUnit) of the smoothing kernel, rebuilt only when the diagram width
    // changes (the reach and the decay unit are both derived from it) instead of per frame.
    float[]? smoothingKernelFactors;
    // The same factors for the right-hand taps: mult / (mult + 1), precomputed (see SmoothenFftData).
    float[]? smoothingKernelRightFactors;
    int smoothingKernelWidth = -1;

    public async Task<float[]> GetScaledAndSlicedFftData(int targetArraySize)
    {
        // This should be rare
        if (mappedData == null || mappedData.Length != targetArraySize)
        {
            mappedData = new float[targetArraySize];
        }

        // While the system audio capture is active (see the options view) the diagram shows what the
        // operating system outputs - every application, not the song. The song's stored volume is then
        // irrelevant; the capture brings its own divisor, which is the smoothed RMS of the stream and
        // therefore the exact counterpart of the per-song RMS the other branch divides by (see
        // SystemAudioCaptureService.VolumeDivisor).
        bool capturingSystemAudio = systemAudioCaptureService.IsCapturing;
        float[] fftData;
        float volumeDivisor;
        if (capturingSystemAudio)
        {
            fftData = audioLibWrapperService.GetFftSpectrumData(ReadSystemAudioAnalysisWindow());
            // Read after the window: reading it also updates the loudness estimate.
            volumeDivisor = systemAudioCaptureService.VolumeDivisor;
        }
        else
        {
            volumeDivisor = GetVolumeDivisor(songPlaybackService.CurrentlyPlaying);
            fftData = await audioLibWrapperService.GetCurrentFftSpectrumData();
        }
        int binCount = fftData.Length;
        if (binCount == 0)
            return mappedData;

        // Scale the bins (in place - the analyzer rewrites its buffer on the next analysis anyway).
        EnsureBinScaleFactors(binCount);
        for (int i = 0; i < binCount; i++)
        {
            fftData[i] *= binScaleFactors![i];
        }

        // Logarithmically scale the x-axis of the FFT data and chop off a slice. The per-column bin
        // ranges are precomputed, the per-column peak itself still runs per frame. Each column takes
        // the MAXIMUM bin of its range (like the old DXMG client): the wide high-frequency columns
        // then stay at their peaks instead of being averaged down, so the few-bin low-frequency
        // columns no longer read oversized relative to the rest of the spectrum.
        EnsureColumnBinRanges(binCount, targetArraySize);
        for (int i = 0; i < targetArraySize; i++)
        {
            mappedData[i] = GetMaxHeight(fftData, columnBinRangeFrom![i], columnBinRangeTo![i]) / volumeDivisor;
        }

        return mappedData;
    }

    /// <summary>
    /// Fills (and returns) the analysis window of the system audio capture with the newest captured
    /// samples. The window is exactly <see cref="AudioLibWrapperService.CurrentFftAnalysisSize"/> samples
    /// long, so the whole window is analyzed - "centred" makes no sense for a live stream, the interesting
    /// audio is the audio that is playing right now. Only valid until the next call.
    /// </summary>
    ReadOnlySpan<float> ReadSystemAudioAnalysisWindow()
    {
        int analysisSize = audioLibWrapperService.CurrentFftAnalysisSize;
        if (systemAudioAnalysisWindow == null || systemAudioAnalysisWindow.Length != analysisSize)
            systemAudioAnalysisWindow = new float[analysisSize];

        // Nothing captured yet (just enabled, or silence since the device has no data) reads as a full
        // window of silence, so the first frames draw an empty diagram instead of stale samples.
        systemAudioCaptureService.TryReadNewestSamples(systemAudioAnalysisWindow);
        return systemAudioAnalysisWindow;
    }

    /// <summary>
    /// Returns the sample window the "Samples" visualization mode draws: the newest captured system audio
    /// while the system audio capture is active, otherwise the song's window around the playback position
    /// (see <see cref="AudioLibWrapperService.GetCurrentlyPlayingSampleData"/>). Like there, the returned
    /// memory is only valid until the next call.
    /// </summary>
    public async Task<ReadOnlyMemory<float>> GetCurrentDiagramSampleData()
    {
        if (systemAudioCaptureService.IsCapturing)
        {
            int windowSize = AudioLibWrapperService.FFT_BUFFER_32BIT_FLOAT_SIZE;
            if (systemAudioSamplesWindow == null || systemAudioSamplesWindow.Length != windowSize)
                systemAudioSamplesWindow = new float[windowSize];

            systemAudioCaptureService.TryReadNewestSamples(systemAudioSamplesWindow);
            return systemAudioSamplesWindow;
        }

        return await audioLibWrapperService.GetCurrentlyPlayingSampleData();
    }

    /// <summary>
    /// Returns the divisor applied to the mapped columns of the currently playing song. Resolved via
    /// the DB once per song and cached, so the diagram no longer opens an EF context on every frame.
    /// The cache is invalidated when the song changes, when the stored volume of the current song was
    /// (re)measured (<see cref="SongVolumeService.VolumeDataChanged"/>, e.g. a song that first played
    /// with a GlobalArray read gets its volume set while still playing) and - as a fallback for other
    /// writers like sync pulls - after <see cref="VolumeCacheMaxAgeMs"/>.
    /// </summary>
    float GetVolumeDivisor(AvailableSong? currentSong)
    {
        if (currentSong?.UpvotedSongId is not Guid songId || songId == Guid.Empty)
            return 1f;

        bool songChanged = volumeCacheSongId != songId;
        bool volumeDataChanged = volumeCacheVersionAtQuery != Volatile.Read(ref volumeCacheInvalidationVersion);
        bool stale = Environment.TickCount64 - volumeCacheLastQueryTimestamp >= VolumeCacheMaxAgeMs;
        if (!songChanged && !volumeDataChanged && !stale)
            return volumeCacheDivisor;

        using var dbContext = dbWrapperService.GetContext();
        var upvotedSong = dbContext.GetUpvotedSongByIdOrNull(songId);
        float volume = upvotedSong?.Volume ?? 0f;
        volumeCacheSongId = songId;
        volumeCacheDivisor = volume > 0 ? volume : 1f;
        volumeCacheLastQueryTimestamp = Environment.TickCount64;
        volumeCacheVersionAtQuery = Volatile.Read(ref volumeCacheInvalidationVersion);
        return volumeCacheDivisor;
    }

    void EnsureBinScaleFactors(int binCount)
    {
        if (binScaleFactors != null && binScaleFactorCount == binCount)
            return;

        // The analysis FFT size follows the bin count (the analyzer exposes fftSize / 2 bins), and low
        // power mode lowers it from 16384 to 4096 samples. Two things change with it and both shrink the
        // displayed values: an unnormalized FFT magnitude grows with the FFT size (roughly proportionally
        // for the tonal peaks that the per-column max picks out), and the sqrt(i + 1) pre-emphasis is
        // tied to the bin index, so the same frequency lands on a lower bin (and thus a smaller factor)
        // when there are fewer bins. Low power mode therefore came out about (16384/4096)^1.5 = 8x too
        // short. Multiplying that ratio back out keeps the diagram height independent of the analysis
        // resolution; at the full size the factor is exactly 1, so nothing changes there.
        double fftSize = binCount * 2.0;
        double sizeCorrection = Math.Pow(AudioLibWrapperService.FFT_BUFFER_32BIT_FLOAT_SIZE / fftSize, 1.5);

        binScaleFactors = new float[binCount];
        for (int i = 0; i < binCount; i++)
            binScaleFactors[i] = (float)(Math.Sqrt(i + 1) * sizeCorrection / FFT_WINDOW_VALUE_DIVISOR);
        binScaleFactorCount = binCount;
    }

    void EnsureColumnBinRanges(int binCount, int columnCount)
    {
        if (columnBinRangeFrom != null && columnBinRangeTo != null
            && columnBinRangeBinCount == binCount && columnBinRangeColumnCount == columnCount)
            return;

        if (columnBinRangeFrom == null || columnBinRangeFrom.Length != columnCount)
        {
            columnBinRangeFrom = new int[columnCount];
            columnBinRangeTo = new int[columnCount];
        }

        int[] froms = columnBinRangeFrom!;
        int[] tos = columnBinRangeTo!;
        double ReadEnd = binCount - (binCount * FFT_WINDOW_PERCENT_CHOPPED_END);
        double ReadStart = (binCount * FFT_WINDOW_PERCENT_CHOPPED_BEGINNING) - 1;

        // Accent warp: walk the log position along the columns using the local column density (see
        // ColumnDensity) instead of stepping a constant 1 / columnCount. The densities are normalized so
        // the accumulated exponents still run from 0 (first column) to exactly 1 (last column), which
        // keeps the overall frequency range and the column count identical to the un-accented mapping.
        double Range = ReadEnd - ReadStart;
        double invColumns = 1.0 / columnCount;
        double densitySum = 0;
        for (int i = 0; i < columnCount; i++)
            densitySum += ColumnDensity(i * invColumns);
        double densityScale = densitySum > 0 ? columnCount / densitySum : 1.0;

        // froms[i] / tos[i] use the column boundary before / at column i, matching the (i - 1) / columnCount
        // and i / columnCount of the plain mapping.
        double previousExponent = -ColumnDensity(0) * densityScale * invColumns;
        double exponent = 0;
        for (int i = 0; i < columnCount; i++)
        {
            froms[i] = (int)(ReadStart + Math.Pow(Range, previousExponent));
            tos[i] = (int)(ReadStart + Math.Pow(Range, exponent));

            previousExponent = exponent;
            exponent += ColumnDensity(i * invColumns) * densityScale * invColumns;
        }

        columnBinRangeBinCount = binCount;
        columnBinRangeColumnCount = columnCount;
    }

    /// <summary>
    /// Logistic sigmoid of a normalized log position, ramping from 0 to 1 around <paramref name="center"/>
    /// over roughly <paramref name="width"/>. Used to build <see cref="ColumnDensity"/>.
    /// </summary>
    private static double SigmoidAccent(double t, double center, double width)
        => 1.0 / (1.0 + Math.Exp(-(t - center) / width));

    /// <summary>
    /// Local "column density" of the accent warp: how many log-frequency positions one column advances
    /// at the normalized axis position <paramref name="t"/> (0 = first column, 1 = last column).
    /// A density of 1 is the plain log mapping, above 1 squeezes that range into fewer columns (thinner
    /// bars) and below 1 spreads it over more columns. The low and the high side ramp their own density
    /// with their own center, width and strength, so the axis space for the band in between can be taken
    /// from whichever side bothers the eye less. Both strengths at 0 give a density of exactly 1
    /// everywhere, which reproduces the mapping without any accent.
    /// </summary>
    private static double ColumnDensity(double t)
        => 1.0
           + FFT_WINDOW_ACCENT_LOW_STRENGTH * (1.0 - SigmoidAccent(t, FFT_WINDOW_ACCENT_LOW_CENTER, FFT_WINDOW_ACCENT_LOW_WIDTH))
           + FFT_WINDOW_ACCENT_HIGH_STRENGTH * SigmoidAccent(t, FFT_WINDOW_ACCENT_HIGH_CENTER, FFT_WINDOW_ACCENT_HIGH_WIDTH);

    private static float GetMaxHeight(float[] array, int from, int to)
    {
        if (from < 0)
            from = 0;

        if (from >= to)
            to = from + 1;

        if (to > array.Length)
            to = array.Length;

        float max = 0;
        for (int i = from; i < to; i++)
            if (array[i] > max)
                max = array[i];

        return max;
    }

    public async Task<float[]> SmoothenFftData(float[] rawData, int targetArraySize, float maxHeight)
    {
        // This should be rare
        if (smoothedData == null || smoothedData.Length != targetArraySize)
        {
            smoothedData = new float[targetArraySize];
        }

        // Clear array
        for (int i = 0; i < smoothedData.Length; i++)
            smoothedData[i] = 0;

        // Replace values with gaussian pillars
        for (int x = 0; x < rawData.Length; x++)
        {
            int Min = x - (int)(THETA * 2.5f); if (Min < 0) Min = 0;
            int Max = x + (int)(THETA * 2.5f); if (Max > smoothedData.Length) Max = smoothedData.Length;

            var NullGaussian = gaussianCache.GetGaussian(0);
            float input = rawData[x];

            for (int y = Min; y < Max; y++)
            {
                float value = gaussianCache.GetGaussian(Math.Abs(x - y)) * input * maxHeight / NullGaussian;
                if (value > smoothedData[y])
                    smoothedData[y] = value;
            }
        }

        // Enforce max height
        for (int i = 0; i < smoothedData.Length; i++)
            if (smoothedData[i] > maxHeight)
                smoothedData[i] = maxHeight;

        // Smoothen. Both the reach and the decay unit scale with the diagram width (see
        // SMOOTHING_KERNEL_REACH_FRACTION), so this is the DXMG kernel: 6*scaleMult samples decaying
        // with 2^(-j/scaleMult), just expressed as a fraction of the width.
        float[] kernelFactors = EnsureSmoothingKernelFactors(smoothedData.Length);
        float[] kernelRightFactors = EnsureSmoothingKernelRightFactors(smoothedData.Length);

        // The tap loop below applies exactly the same operations in exactly the same order as
        // "for j: if (i > j) left-tap; if (i < length-1-j) right-tap", it just splits the tile ends into
        // their own ranges so the two range checks and the per-tap division are gone: with the
        // width-scaled kernel this pass is the most expensive part of a diagram frame (a wide window
        // gives it ~50 taps per column). The accumulated value is kept in a local and written back once
        // - reading and writing the same float slot gives the same bits, so only the precomputed
        // right-hand multiplier (mult/(mult+1) instead of mult/(mult+1) per tap) changes the result, by
        // about 1e-7 of a bar height.
        int length = smoothedData.Length;
        int tapCount = kernelFactors.Length;
        for (int i = 0; i < length; i++)
        {
            float current = smoothedData[i];

            // Taps whose left AND right neighbour both exist.
            int bothEnd = Math.Min(tapCount, Math.Min(i, length - 1 - i));
            for (int j = 0; j < bothEnd; j++)
            {
                current += (smoothedData[i - 1 - j] - current) * kernelFactors[j];
                current += (smoothedData[i + 1 + j] - current) * kernelRightFactors[j];
            }

            // Then the taps of the side that still has room in that direction.
            if (i <= length - 1 - i)
            {
                int rightEnd = Math.Min(tapCount, length - 1 - i);
                for (int j = bothEnd; j < rightEnd; j++)
                    current += (smoothedData[i + 1 + j] - current) * kernelRightFactors[j];
            }
            else
            {
                int leftEnd = Math.Min(tapCount, i);
                for (int j = bothEnd; j < leftEnd; j++)
                    current += (smoothedData[i - 1 - j] - current) * kernelFactors[j];
            }

            smoothedData[i] = current;
        }

        return smoothedData;
    }

    /// <summary>
    /// Returns the decay factors of the smoothing kernel for a diagram of <paramref name="width"/>
    /// columns: the samples count 2^(-j / decayUnit) with the reach (width * 6/430) and the decay unit
    /// (width/430, i.e. one DXMG scaleMult step) both derived from the width. Cached per width, so the
    /// per-frame smoothing neither allocates nor recomputes them.
    /// </summary>
    float[] EnsureSmoothingKernelFactors(int width)
    {
        if (smoothingKernelFactors != null && smoothingKernelWidth == width)
            return smoothingKernelFactors;

        int sampleCount = (int)(width * SMOOTHING_KERNEL_REACH_FRACTION);
        if (sampleCount < 1)
            sampleCount = 1;

        if (smoothingKernelFactors == null || smoothingKernelFactors.Length != sampleCount)
            smoothingKernelFactors = new float[sampleCount];

        double decayUnit = width * SMOOTHING_KERNEL_REACH_FRACTION / 6.0;
        for (int j = 0; j < sampleCount; j++)
            smoothingKernelFactors[j] = (float)Math.Pow(2.0, -j / decayUnit);

        // The right-hand taps of the smoothing pass use mult / (mult + 1); precomputing it removes a
        // division per tap per column (the pass runs width * taps times, so this is thousands of
        // divisions per frame). Same value within one float rounding step.
        if (smoothingKernelRightFactors == null || smoothingKernelRightFactors.Length != sampleCount)
            smoothingKernelRightFactors = new float[sampleCount];
        for (int j = 0; j < sampleCount; j++)
            smoothingKernelRightFactors[j] = smoothingKernelFactors[j] / (smoothingKernelFactors[j] + 1f);

        smoothingKernelWidth = width;
        return smoothingKernelFactors;
    }

    /// <summary>
    /// The right-hand multipliers of the smoothing kernel (see
    /// <see cref="EnsureSmoothingKernelFactors"/>), cached for the same width.
    /// </summary>
    float[] EnsureSmoothingKernelRightFactors(int width)
    {
        EnsureSmoothingKernelFactors(width);
        return smoothingKernelRightFactors!;
    }
}