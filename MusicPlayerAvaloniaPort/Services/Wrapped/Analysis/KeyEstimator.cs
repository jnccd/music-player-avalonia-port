using System;

namespace MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

/// <summary>Result of a key estimate.</summary>
public readonly record struct KeyEstimate(string KeyName, bool IsMinor, float Correlation, float Adherence)
{
    public static KeyEstimate None => new("", false, 0f, 0f);
}

/// <summary>
/// Key estimation by correlating the song's chroma (pitch class histogram) with the Krumhansl-Kessler
/// key profiles - the classic tonal-hierarchy templates, rotated over all 24 major/minor keys.
/// <para>
/// Two caveats are reported instead of hidden: the raw correlation (<see cref="KeyEstimate.Correlation"/>,
/// the "how well does this template fit at all" number) and the adhesion
/// (<see cref="KeyEstimate.Adherence"/>, the share of the chroma energy that actually falls inside the
/// estimated key's scale). A song with a low correlation is atonal, modal or simply too percussive for a
/// key to be meaningful, and the wrapped is expected to say so rather than print an invented key.
/// </para>
/// </summary>
public static class KeyEstimator
{
    static readonly string[] PitchClassNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    // Krumhansl-Kessler probe tone profiles.
    static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>
    /// Estimates the key from a normalized 12 element chroma vector (index 0 = C). Returns
    /// <see cref="KeyEstimate.None"/> when the chroma carries no usable tonal content.
    /// </summary>
    public static KeyEstimate Estimate(float[] chroma)
    {
        if (chroma.Length < 12)
            return KeyEstimate.None;

        double total = 0.0;
        for (int i = 0; i < 12; i++)
            total += chroma[i];
        if (total <= 1e-6)
            return KeyEstimate.None;

        double bestCorrelation = double.NegativeInfinity;
        int bestRoot = 0;
        bool bestMinor = false;

        for (int root = 0; root < 12; root++)
        {
            double major = Correlate(chroma, MajorProfile, root);
            if (major > bestCorrelation)
            {
                bestCorrelation = major;
                bestRoot = root;
                bestMinor = false;
            }

            double minor = Correlate(chroma, MinorProfile, root);
            if (minor > bestCorrelation)
            {
                bestCorrelation = minor;
                bestRoot = root;
                bestMinor = true;
            }
        }

        // Below this the "best" template is not meaningfully better than any other rotation.
        if (bestCorrelation < 0.4)
            return KeyEstimate.None;

        var scale = bestMinor
            ? new[] { 0, 2, 3, 5, 7, 8, 10 }  // natural minor
            : new[] { 0, 2, 4, 5, 7, 9, 11 }; // major

        double inScale = 0.0;
        foreach (int interval in scale)
            inScale += chroma[(bestRoot + interval) % 12];

        return new KeyEstimate(
            $"{PitchClassNames[bestRoot]} {(bestMinor ? "minor" : "major")}",
            bestMinor,
            (float)bestCorrelation,
            (float)Math.Clamp(inScale / total, 0.0, 1.0));
    }

    /// <summary>Pearson correlation of the chroma with a key profile rotated to <paramref name="root"/>.</summary>
    static double Correlate(float[] chroma, double[] profile, int root)
    {
        double chromaMean = 0.0;
        double profileMean = 0.0;
        for (int i = 0; i < 12; i++)
        {
            chromaMean += chroma[i];
            profileMean += profile[i];
        }
        chromaMean /= 12.0;
        profileMean /= 12.0;

        double covariance = 0.0;
        double chromaVariance = 0.0;
        double profileVariance = 0.0;
        for (int i = 0; i < 12; i++)
        {
            double chromaValue = chroma[(root + i) % 12] - chromaMean;
            double profileValue = profile[i] - profileMean;
            covariance += chromaValue * profileValue;
            chromaVariance += chromaValue * chromaValue;
            profileVariance += profileValue * profileValue;
        }

        double denominator = Math.Sqrt(chromaVariance * profileVariance);
        return denominator > 1e-12 ? covariance / denominator : 0.0;
    }
}
