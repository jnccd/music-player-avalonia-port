using System;

namespace MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

/// <summary>
/// Triangular mel filterbank (standard HTK-style construction) that reduces a linear power spectrum to
/// mel band energies.
/// <para>
/// It serves the two halves of the audio analysis at once: the mel band energies are the input of the
/// spectral flux the tempo estimator correlates (a mel scale weights the musically relevant low
/// frequencies far more strongly than a linear one, which is what makes onset detection work on real
/// mp3 material), and their logarithm is the input of the DCT that produces the MFCC timbre vector.
/// </para>
/// </summary>
public sealed class MelFilterbank
{
    public int FilterCount { get; }
    public int BinCount { get; }

    readonly int[] startBin;
    readonly int[] endBin;
    readonly float[][] weights;

    /// <param name="filterCount">Number of triangular bands.</param>
    /// <param name="fftSize">FFT size the power spectra are computed with.</param>
    /// <param name="sampleRate">Sample rate of the analysed signal.</param>
    /// <param name="minFrequencyHz">Lower edge of the first band.</param>
    /// <param name="maxFrequencyHz">Upper edge of the last band (clamped to Nyquist).</param>
    public MelFilterbank(int filterCount, int fftSize, int sampleRate, double minFrequencyHz = 40.0, double maxFrequencyHz = 8000.0)
    {
        if (filterCount < 1)
            throw new ArgumentOutOfRangeException(nameof(filterCount));

        FilterCount = filterCount;
        BinCount = fftSize / 2 + 1;

        double nyquist = sampleRate / 2.0;
        double lowMel = FrequencyToMel(Math.Max(1.0, minFrequencyHz));
        double highMel = FrequencyToMel(Math.Min(maxFrequencyHz, nyquist));
        if (highMel <= lowMel)
            throw new ArgumentException("Empty mel range.", nameof(maxFrequencyHz));

        // filterCount + 2 equally spaced mel points: band i rises from point i to i+1 and falls to i+2.
        var points = new int[filterCount + 2];
        for (int i = 0; i < points.Length; i++)
        {
            double mel = lowMel + (highMel - lowMel) * i / (filterCount + 1);
            double frequency = MelToFrequency(mel);
            points[i] = Math.Clamp((int)Math.Round(frequency / nyquist * (BinCount - 1)), 0, BinCount - 1);
        }

        startBin = new int[filterCount];
        endBin = new int[filterCount];
        weights = new float[filterCount][];

        var binFrequency = new double[BinCount];
        for (int k = 0; k < BinCount; k++)
            binFrequency[k] = k * nyquist / (BinCount - 1);

        for (int i = 0; i < filterCount; i++)
        {
            int start = points[i];
            int center = Math.Max(points[i + 1], start + 1);
            int end = Math.Max(points[i + 2], center + 1);

            startBin[i] = start;
            // The upper edge is clamped by the last band, so remember the true edge for the weights.
            int clampedEnd = Math.Min(end, BinCount - 1);
            endBin[i] = clampedEnd;

            var bandWeights = new float[clampedEnd - start + 1];
            double centerFrequency = binFrequency[center];
            double startFrequency = binFrequency[start];
            double endFrequency = binFrequency[end < BinCount ? end : BinCount - 1];
            for (int k = start; k <= clampedEnd; k++)
            {
                double frequency = binFrequency[k];
                double weight;
                if (frequency <= centerFrequency)
                    weight = (centerFrequency - startFrequency) < 1e-9 ? 0.0 : (frequency - startFrequency) / (centerFrequency - startFrequency);
                else
                    weight = (endFrequency - centerFrequency) < 1e-9 ? 0.0 : (endFrequency - frequency) / (endFrequency - centerFrequency);
                bandWeights[k - start] = (float)Math.Max(0.0, weight);
            }
            weights[i] = bandWeights;
        }
    }

    public static double FrequencyToMel(double frequencyHz) => 2595.0 * Math.Log10(1.0 + frequencyHz / 700.0);

    public static double MelToFrequency(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    /// <summary>
    /// Applies the filterbank to a power spectrum, writing one band energy per filter into
    /// <paramref name="melEnergies"/>.
    /// </summary>
    public void Apply(ReadOnlySpan<float> powerSpectrum, Span<float> melEnergies)
    {
        if (powerSpectrum.Length < BinCount)
            throw new ArgumentException($"Power spectrum must hold at least {BinCount} bins.", nameof(powerSpectrum));
        if (melEnergies.Length < FilterCount)
            throw new ArgumentException($"Mel energy buffer must hold at least {FilterCount} values.", nameof(melEnergies));

        for (int filter = 0; filter < FilterCount; filter++)
        {
            int start = startBin[filter];
            var bandWeights = weights[filter];
            float sum = 0f;
            for (int i = 0; i < bandWeights.Length; i++)
                sum += bandWeights[i] * powerSpectrum[start + i];
            melEnergies[filter] = sum;
        }
    }

    /// <summary>
    /// DCT-II of the log mel energies into <paramref name="cepstrum"/>: the mel frequency cepstral
    /// coefficients. <paramref name="melEnergies"/> holds raw (not yet logarithmic) band energies; a
    /// small floor keeps a silent frame from producing -infinity.
    /// </summary>
    public static void LogMelToCepstrum(ReadOnlySpan<float> melEnergies, Span<float> cepstrum)
    {
        int bands = melEnergies.Length;
        int count = Math.Min(cepstrum.Length, bands);
        double scale = Math.PI / bands;

        for (int c = 0; c < count; c++)
        {
            double sum = 0.0;
            for (int b = 0; b < bands; b++)
                sum += Math.Log(Math.Max(melEnergies[b], 1e-10f)) * Math.Cos(scale * (b + 0.5) * c);
            cepstrum[c] = (float)(sum * Math.Sqrt(2.0 / bands));
        }
    }
}
