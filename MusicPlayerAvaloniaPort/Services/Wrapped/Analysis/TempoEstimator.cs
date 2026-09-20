using System;
using System.Collections.Generic;

namespace MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

/// <summary>Result of a tempo estimate: the folded BPM, the raw search result and a confidence.</summary>
public readonly record struct TempoEstimate(float Bpm, float RawBpm, float Confidence, bool WasFolded)
{
    public static TempoEstimate None => new(0f, 0f, 0f, false);
}

/// <summary>
/// Tempo estimation from an onset strength signal (mel spectral flux).
/// <para>
/// It is built around the two problems that make naive autocorrelation tempo detection useless on a real
/// library:
/// </para>
/// <list type="number">
/// <item>
/// <b>Octave errors.</b> The autocorrelation of a beat signal has strong peaks at 1x, 2x, 3x ... the beat
/// period, so "half tempo" and "double tempo" score almost as well as the truth - which is why so much
/// software reports 87 BPM for a 174 BPM drum &amp; bass track. The scoring here sums the
/// autocorrelation over several multiples of the candidate period (a comb), which favours the
/// fundamental and makes the halved/doubled candidates compete on evidence instead of winning by
/// accident.
/// </item>
/// <item>
/// <b>Plausibility.</b> Candidates outside 70..180 BPM carry a penalty, and the reported value is folded
/// into that range by doubling/halving - so a 174 BPM track is reported as 174 (not 87) while the raw
/// search result is kept next to it rather than hidden.
/// </item>
/// </list>
/// Quiet, beat-less material (ambient, classical, spoken word) gets a low confidence instead of a fake
/// precise number, and the wrapped shows that caveat.
/// </summary>
public static class TempoEstimator
{
    /// <summary>BPM range the reported tempo is folded into.</summary>
    public const float MinBpm = 70f;
    public const float MaxBpm = 180f;

    /// <summary>BPM range the search itself runs over (wider than the fold range, so folding has material).</summary>
    const float SearchMinBpm = 55f;
    const float SearchMaxBpm = 240f;
    const float BpmStep = 0.1f;

    /// <summary>How many multiples of the beat period contribute to the comb score.</summary>
    const int CombHarmonics = 4;

    /// <summary>
    /// Estimates the tempo of an onset strength signal (one value per analysis frame).
    /// </summary>
    public static TempoEstimate Estimate(ReadOnlySpan<float> onsetStrength, double framesPerSecond)
    {
        if (onsetStrength.Length < 32 || framesPerSecond <= 0)
            return TempoEstimate.None;

        // Remove the DC offset: a non-zero mean produces a large autocorrelation at every lag, which
        // drowns the actual periodicity.
        int n = onsetStrength.Length;
        double mean = 0.0;
        for (int i = 0; i < n; i++)
            mean += onsetStrength[i];
        mean /= n;

        var signal = new double[n];
        double energy = 0.0;
        for (int i = 0; i < n; i++)
        {
            signal[i] = onsetStrength[i] - mean;
            energy += signal[i] * signal[i];
        }
        if (energy <= 1e-9)
            return TempoEstimate.None;

        int minLag = Math.Max(1, (int)Math.Floor(framesPerSecond * 60.0 / SearchMaxBpm));
        int maxLag = Math.Min(n - 2, (int)Math.Ceiling(framesPerSecond * 60.0 / SearchMinBpm));
        if (maxLag <= minLag + 1)
            return TempoEstimate.None;

        var autocorrelation = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0.0;
            int count = n - lag;
            for (int i = 0; i < count; i++)
                sum += signal[i] * signal[i + lag];
            autocorrelation[lag] = count > 0 ? sum / count : 0.0;
        }

        // Scores are kept with their lags: the confidence below compares the winner against the best
        // *distant* candidate (a competing tempo hypothesis), not against its own neighbours - adjacent
        // BPM values always score almost identically, so a "second best" 0.2 BPM away would make every
        // estimate look like a coin flip.
        var lags = new List<double>(2048);
        var scores = new List<double>(2048);
        double bestScore = double.NegativeInfinity;
        double bestLag = 0;

        for (float bpm = SearchMinBpm; bpm <= SearchMaxBpm; bpm += BpmStep)
        {
            double lag = framesPerSecond * 60.0 / bpm;
            if (lag < minLag || lag > maxLag)
                continue;

            double score = 0.0;
            for (int harmonic = 1; harmonic <= CombHarmonics; harmonic++)
            {
                double harmonicLag = lag * harmonic;
                if (harmonicLag > maxLag)
                    break;
                score += Interpolate(autocorrelation, harmonicLag) / harmonic;
            }

            // Plausibility weighting: inside the fold range nothing is penalized; towards the edges of the
            // search range the score decays, so an equally plausible in-range tempo always wins.
            if (bpm < MinBpm)
                score *= 1.0 + 0.6 * (bpm - MinBpm) / (MinBpm - SearchMinBpm);
            else if (bpm > MaxBpm)
                score *= 1.0 + 0.6 * (MaxBpm - bpm) / (SearchMaxBpm - MaxBpm);

            lags.Add(lag);
            scores.Add(score);

            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (bestLag <= 0 || bestScore <= 0)
            return TempoEstimate.None;

        double runnerUp = 0.0;
        for (int i = 0; i < lags.Count; i++)
            if (Math.Abs(lags[i] - bestLag) / bestLag > 0.05 && scores[i] > runnerUp)
                runnerUp = scores[i];

        float confidence = (float)Math.Clamp((bestScore - runnerUp) / bestScore, 0.0, 1.0);

        // Sub-step refinement: a parabola through the autocorrelation around the winning lag. The 0.1 BPM
        // grid is coarser than the difference between, say, 127.5 and 128, and rounding every song onto
        // the same grid would make a library's tempo distribution look artificially clustered.
        double refinedLag = RefineLag(autocorrelation, bestLag);
        double rawBpm = framesPerSecond * 60.0 / refinedLag;

        double folded = Fold(rawBpm, out bool wasFolded);
        return new TempoEstimate((float)folded, (float)rawBpm, confidence, wasFolded);
    }

    /// <summary>Parabolic interpolation of the autocorrelation maximum around <paramref name="lag"/>.</summary>
    static double RefineLag(double[] autocorrelation, double lag)
    {
        int center = (int)Math.Round(lag);
        if (center <= 1 || center >= autocorrelation.Length - 1)
            return lag;

        double left = autocorrelation[center - 1];
        double middle = autocorrelation[center];
        double right = autocorrelation[center + 1];
        double denominator = left - 2.0 * middle + right;
        if (Math.Abs(denominator) < 1e-12)
            return lag;

        double shift = 0.5 * (left - right) / denominator;
        return Math.Clamp(center + shift, center - 1.0, center + 1.0);
    }

    static double Interpolate(double[] values, double index)
    {
        int lower = (int)Math.Floor(index);
        if (lower < 0)
            return values[0];
        if (lower >= values.Length - 1)
            return values[^1];

        double fraction = index - lower;
        return values[lower] * (1.0 - fraction) + values[lower + 1] * fraction;
    }

    /// <summary>
    /// Folds a tempo into the 70..180 BPM range by doubling/halving. A presentation decision made
    /// explicit: the raw estimate stays available so the wrapped can say "174 BPM (measured 87)" instead
    /// of hiding the ambiguity.
    /// </summary>
    public static double Fold(double bpm, out bool wasFolded)
    {
        wasFolded = false;
        if (bpm <= 0)
            return 0;

        while (bpm < MinBpm)
        {
            bpm *= 2.0;
            wasFolded = true;
        }
        while (bpm >= MaxBpm)
        {
            bpm /= 2.0;
            wasFolded = true;
        }
        return bpm;
    }

    /// <summary>
    /// Onset detection function: half-wave rectified mel spectral flux with an adaptive threshold. A
    /// frame is an onset when its flux exceeds the local mean plus one standard deviation, which keeps
    /// the function usable both for compressed modern masters and for quiet soundtrack material.
    /// </summary>
    public static float[] OnsetStrength(float[] fluxPerFrame, out float onsetDensityPerSecond, double framesPerSecond)
    {
        var result = new float[fluxPerFrame.Length];
        int onsets = 0;

        const int windowRadius = 16; // ~0.18 s of context on each side at 86 frames/s
        for (int i = 0; i < fluxPerFrame.Length; i++)
        {
            int from = Math.Max(0, i - windowRadius);
            int to = Math.Min(fluxPerFrame.Length - 1, i + windowRadius);
            double sum = 0.0;
            double sumSquares = 0.0;
            int count = 0;
            for (int j = from; j <= to; j++)
            {
                double neighbour = fluxPerFrame[j];
                sum += neighbour;
                sumSquares += neighbour * neighbour;
                count++;
            }
            double mean = sum / count;
            double variance = Math.Max(0.0, sumSquares / count - mean * mean);
            double threshold = mean + Math.Sqrt(variance);

            float value = (float)Math.Max(0.0, fluxPerFrame[i] - threshold);
            result[i] = value;
            if (value > 0f)
                onsets++;
        }

        double seconds = framesPerSecond > 0 ? fluxPerFrame.Length / framesPerSecond : 0;
        onsetDensityPerSecond = seconds > 0 ? (float)(onsets / seconds) : 0f;
        return result;
    }
}
