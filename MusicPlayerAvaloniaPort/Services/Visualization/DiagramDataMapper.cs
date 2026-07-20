using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Visualization;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(DiagramDataMapper))]
public class DiagramDataMapper(AudioLibWrapperService audioLibWrapperService, SongPlaybackService songPlaybackService, DbWrapperService dbWrapperService)
{
    private float[]? smoothedData;
    private float[]? mappedData;
    private const float THETA = 3.0f;
    private GaussianCache gaussianCache = new GaussianCache(THETA);
    private float[] hammingWindowFactorArray = Enumerable
        .Range(0, AudioLibWrapperService.FFT_BUFFER_SIZE)
        .Select(i => (float)HammingWindowCache.ComputeHammingWindow(i, AudioLibWrapperService.FFT_BUFFER_SIZE))
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
            fftData[i] = fftData[i] * (float)Math.Sqrt(i + 1) / 100;
        }

        // Logarithmically scale the x-axis of the FFT data and chop of a slice
        float ReadLength = fftData.Length / 1.9f;
        int startValue = 30;
        for (int i = 0; i < targetArraySize; i++)
        {
            double lastindex = Math.Pow(ReadLength, (startValue + i - 1) / (double)targetArraySize);
            double index = Math.Pow(ReadLength, (startValue + i) / (double)targetArraySize);
            mappedData[i] = GetMaxHeight(fftData, (int)lastindex, (int)index) * (currentUpvotedSong.Volume > 0 ? currentUpvotedSong.Volume : 1);
        }

        return mappedData;
    }

    private static float Sigmoid(float value)
    {
        return 1.0f / (1.0f + (float)Math.Exp(-value));
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