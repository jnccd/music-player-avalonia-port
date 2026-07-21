using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Visualization;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(DiagramDataMapperService))]
public class DiagramDataMapperService(AudioLibWrapperService audioLibWrapperService, SongPlaybackService songPlaybackService, DbWrapperService dbWrapperService)
{
    private const double FFT_WINDOW_PERCENT_CHOPPED_BEGINNING = 0.08;
    private const double FFT_WINDOW_PERCENT_CHOPPED_END = 0.4;
    private const float FFT_WINDOW_VALUE_DIVISOR = 3500;
    private const double FFT_SAMPLES_HAMMING_WINDOW_DOWNWARD_EXPONENT = 2;

    private float[]? smoothedData;
    private float[]? mappedData;

    private const float THETA = 3.0f;
    private GaussianCache gaussianCache = new GaussianCache(THETA);
    private float[] hammingWindowFactorArray = Enumerable
        .Range(0, AudioLibWrapperService.FFT_BUFFER_SIZE)
        .Select(i => (float)Math.Pow(HammingWindowCache.ComputeHammingWindow(i, AudioLibWrapperService.FFT_BUFFER_SIZE), FFT_SAMPLES_HAMMING_WINDOW_DOWNWARD_EXPONENT))
        .ToArray();

    public float[] GetScaledAndSlicedFftData(int targetArraySize)
    {
        // This should be rare
        if (mappedData == null || mappedData.Length != targetArraySize)
        {
            mappedData = new float[targetArraySize];
        }

        var currentSong = songPlaybackService.CurrentlyPlaying;
        var currentUpvotedSong = dbWrapperService.GetContext().GetUpvotedSongById(currentSong?.UpvotedSongId);

        var fftData = audioLibWrapperService.GetCurrentFftSpectrumData(hammingWindowFactorArray);
        for (int i = 0; i < fftData.Length; i++)
        {
            fftData[i] = fftData[i] * (float)Math.Sqrt(i + 1) / FFT_WINDOW_VALUE_DIVISOR * 1;
        }

        // Logarithmically scale the x-axis of the FFT data and chop of a slice
        double ReadEnd = fftData.Length - (fftData.Length * FFT_WINDOW_PERCENT_CHOPPED_END);
        double ReadStart = (fftData.Length * FFT_WINDOW_PERCENT_CHOPPED_BEGINNING) - 1;
        for (int i = 0; i < targetArraySize; i++)
        {
            double lastindex = ReadStart + Math.Pow(ReadEnd - ReadStart, (i - 1) / (double)targetArraySize);
            double index = ReadStart + Math.Pow(ReadEnd - ReadStart, i / (double)targetArraySize);
            //if (i == 0 || i == targetArraySize / 2 || i == targetArraySize - 1) Debug.WriteLine($"{i}: {lastindex} {index}");
            mappedData[i] = GetMaxHeight(fftData, (int)lastindex, (int)index) / (currentUpvotedSong.Volume > 0 ? currentUpvotedSong.Volume : 1);
        }

        return mappedData;
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

    public float[] SmoothenFftData(float[] rawData, int targetArraySize, float maxHeight)
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
        var pow2s = Enumerable.Range(0, maxSamples + 1).Select(j => (float)Math.Pow(2, -j)).ToArray();

        for (int i = 0; i < smoothedData.Length; i++)
        {
            for (int j = 0; j < maxSamples; j++)
            {
                var mult = pow2s[j];

                if (i > j)
                    smoothedData[i] += (smoothedData[i - 1 - j] - smoothedData[i]) * mult;
                if (i < smoothedData.Length - 1 - j)
                    smoothedData[i] += (smoothedData[i + 1 + j] - smoothedData[i]) * mult / (mult + 1);
            }
        }

        return smoothedData;
    }
}