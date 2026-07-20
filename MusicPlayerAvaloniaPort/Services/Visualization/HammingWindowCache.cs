using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;

namespace MusicPlayerAvaloniaPort.Services.Visualization;

public class HammingWindowCache
{
    private readonly float[] HammingWindow;
    private int Length;

    public HammingWindowCache(int Length)
    {
        this.Length = Length;
        HammingWindow = new float[Length];

        for (int i = 0; i < HammingWindow.Length; i++)
            HammingWindow[i] = (float)ComputeHammingWindow(i);
    }

    public float GetHammingWindow(int i)
    {
        if (i >= 0 && i < HammingWindow.Length)
            return HammingWindow[i];
        else
            return 0;
    }

    private double ComputeHammingWindow(int n) => ComputeHammingWindow(n, Length);

    public static double ComputeHammingWindow(int n, int frameSize)
    {
        return 0.54 - 0.46 * Math.Cos(Math.PI * 2.0 * (double)n / (double)(frameSize - 1));
    }
}