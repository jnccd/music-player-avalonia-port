using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;

namespace MusicPlayerAvaloniaPort.Services.Visualization;

public class GaussianCache
{
    private readonly float[] Gaussian;
    private readonly float theta;

    public GaussianCache(float theta)
    {
        this.theta = theta;
        Gaussian = new float[(int)theta * 2];

        for (int i = 0; i < Gaussian.Length; i++)
            Gaussian[i] = ComputeGaussian(i - (int)theta, theta);
    }

    public float GetGaussian(int n)
    {
        if (n > -(int)theta && n < (int)theta)
            return Gaussian[n + (int)theta];
        else
            return 0;
    }

    private float ComputeGaussian(float n, float theta)
    {
        return (float)(1.0 / Math.Sqrt(2 * Math.PI * theta) *
                       Math.Exp(-(n * n) / (2 * theta * theta)));
    }
}