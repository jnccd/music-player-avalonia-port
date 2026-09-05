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

    public DiagramDataMapperService(AudioLibWrapperService audioLibWrapperService, SongPlaybackService songPlaybackService,
        DbWrapperService dbWrapperService, SongVolumeService songVolumeService)
    {
        this.audioLibWrapperService = audioLibWrapperService;
        this.songPlaybackService = songPlaybackService;
        this.dbWrapperService = dbWrapperService;
        this.songVolumeService = songVolumeService;

        // A song that was first played without a stored volume (GlobalArray read) gets its volume
        // measured and stored while it is still playing (see SongVolumeService). The cached divisor
        // below must notice that, or the bars would keep the unscaled height for the whole song.
        this.songVolumeService.VolumeDataChanged += OnVolumeDataChanged;
    }

    void OnVolumeDataChanged() => Interlocked.Increment(ref volumeCacheInvalidationVersion);

    private const double FFT_WINDOW_PERCENT_CHOPPED_BEGINNING = 0.001;
    private const double FFT_WINDOW_PERCENT_CHOPPED_END = 0.6;
    private const float FFT_WINDOW_VALUE_DIVISOR = 9001;
    private const double FFT_SAMPLES_HAMMING_WINDOW_DOWNWARD_EXPONENT = 2;

    private float[]? smoothedData;
    private float[]? mappedData;

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
    /// 1/2^j decay factors for the smoothing pass. Was rebuilt inside <see cref="SmoothenFftData"/> on
    /// every call before, which allocated and recomputed it once per frame.
    /// </summary>
    private static readonly float[] SMOOTHING_POW2S = Enumerable
        .Range(0, 7) // maxSamples (6) + 1
        .Select(j => (float)Math.Pow(2, -j))
        .ToArray();

    public async Task<float[]> GetScaledAndSlicedFftData(int targetArraySize)
    {
        // This should be rare
        if (mappedData == null || mappedData.Length != targetArraySize)
        {
            mappedData = new float[targetArraySize];
        }

        var currentSong = songPlaybackService.CurrentlyPlaying;
        float volumeDivisor = GetVolumeDivisor(currentSong);

        var fftData = await audioLibWrapperService.GetCurrentFftSpectrumData();
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
        // ranges are precomputed, the averaging itself still runs per frame.
        EnsureColumnBinRanges(binCount, targetArraySize);
        for (int i = 0; i < targetArraySize; i++)
        {
            mappedData[i] = GetAvgHeight(fftData, columnBinRangeFrom![i], columnBinRangeTo![i]) / volumeDivisor;
        }

        return mappedData;
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

        binScaleFactors = new float[binCount];
        for (int i = 0; i < binCount; i++)
            binScaleFactors[i] = (float)Math.Sqrt(i + 1) / FFT_WINDOW_VALUE_DIVISOR;
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
        for (int i = 0; i < columnCount; i++)
        {
            double lastindex = ReadStart + Math.Pow(ReadEnd - ReadStart, (i - 1) / (double)columnCount);
            double index = ReadStart + Math.Pow(ReadEnd - ReadStart, i / (double)columnCount);
            froms[i] = (int)lastindex;
            tos[i] = (int)index;
        }

        columnBinRangeBinCount = binCount;
        columnBinRangeColumnCount = columnCount;
    }

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

    private static float GetAvgHeight(float[] array, int from, int to)
    {
        if (from < 0)
            from = 0;

        if (from >= to)
            to = from + 1;

        if (to > array.Length)
            to = array.Length;

        float sum = 0;
        for (int i = from; i < to; i++)
            sum += array[i];
        var avg = sum / (to - from);

        return avg;
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

        // Smoothen
        int maxSamples = 6;

        for (int i = 0; i < smoothedData.Length; i++)
        {
            for (int j = 0; j < maxSamples; j++)
            {
                var mult = SMOOTHING_POW2S[j];

                if (i > j)
                    smoothedData[i] += (smoothedData[i - 1 - j] - smoothedData[i]) * mult;
                if (i < smoothedData.Length - 1 - j)
                    smoothedData[i] += (smoothedData[i + 1 + j] - smoothedData[i]) * mult / (mult + 1);
            }
        }

        return smoothedData;
    }
}