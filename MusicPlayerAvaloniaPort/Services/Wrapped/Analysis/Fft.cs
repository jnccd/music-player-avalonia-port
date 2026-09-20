using System;

namespace MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

/// <summary>
/// Iterative radix-2 Cooley-Tukey FFT plus the window function the wrapped analysis needs.
/// <para>
/// The desktop client already has an FFT, but it is wired into the visualization pipeline and operates
/// on the playback sample window. The wrapped analysis transforms arbitrary blocks of a decoded file in
/// its own worker threads, so it carries its own small implementation instead of coupling to the
/// visualization state.
/// </para>
/// <para>
/// One instance is bound to one transform size and owns its scratch buffers, so a batch analysis reuses
/// it for every frame of every song without allocating. It is not thread safe; the analyzer uses one
/// instance per worker.
/// </para>
/// </summary>
public sealed class Fft
{
    public int Size { get; }

    readonly int halfSize;
    readonly int[] bitReversal;
    readonly float[] cosTable; // cos(2*pi*k/N) for k in [0, N/2)
    readonly float[] sinTable; // sin(2*pi*k/N)
    readonly float[] window;   // periodic Hann window of Size
    readonly float[] scratchRe;
    readonly float[] scratchIm;
    readonly float[] permuteRe;
    readonly float[] permuteIm;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException($"FFT size must be a power of two, got {size}.", nameof(size));

        Size = size;
        halfSize = size / 2;
        bitReversal = new int[size];
        cosTable = new float[halfSize];
        sinTable = new float[halfSize];
        window = new float[size];
        scratchRe = new float[size];
        scratchIm = new float[size];
        permuteRe = new float[size];
        permuteIm = new float[size];

        int bits = 0;
        while (1 << bits < size)
            bits++;
        for (int i = 0; i < size; i++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0)
                    reversed |= 1 << (bits - 1 - b);
            bitReversal[i] = reversed;
        }

        for (int k = 0; k < halfSize; k++)
        {
            double angle = -2.0 * Math.PI * k / size;
            cosTable[k] = (float)Math.Cos(angle);
            sinTable[k] = (float)Math.Sin(angle);
        }

        // Periodic Hann window: w[n] = 0.5 * (1 - cos(2*pi*n/N)). The periodic (not symmetric) form is
        // the correct one for STFT analysis with overlapping frames.
        for (int n = 0; n < size; n++)
            window[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / size)));
    }

    /// <summary>Number of bins <see cref="PowerSpectrum"/> fills (Size/2 + 1, Nyquist included).</summary>
    public int BinCount => halfSize + 1;

    /// <summary>
    /// Computes the power spectrum (|X[k]|^2) of a real block. The block must be exactly <see cref="Size"/>
    /// samples; the window is applied internally.
    /// </summary>
    public void PowerSpectrum(ReadOnlySpan<float> block, Span<float> power)
    {
        if (block.Length != Size)
            throw new ArgumentException($"Expected a block of {Size} samples, got {block.Length}.", nameof(block));
        if (power.Length < BinCount)
            throw new ArgumentException($"Power buffer must hold at least {BinCount} bins.", nameof(power));

        for (int i = 0; i < Size; i++)
            scratchRe[i] = block[i] * window[i];

        // The imaginary part must start at zero: a real input signal has no imaginary component. It is
        // cleared explicitly rather than relying on the permutation below, and the transform reads the
        // windowed samples through its own buffers instead of the caller's span, so no aliasing question
        // can arise between the input and the scratch state.
        Array.Clear(scratchIm);

        Forward(scratchRe, scratchIm);

        for (int k = 0; k < BinCount; k++)
        {
            float re = scratchRe[k];
            float im = scratchIm[k];
            power[k] = re * re + im * im;
        }
    }

    /// <summary>
    /// In-place radix-2 decimation-in-time transform. The bit reversal is copied through dedicated
    /// scratch buffers rather than swapped pairwise, which makes the data flow unambiguous.
    /// </summary>
    void Forward(float[] real, float[] imaginary)
    {
        for (int i = 0; i < Size; i++)
        {
            int j = bitReversal[i];
            permuteRe[i] = real[j];
            permuteIm[i] = imaginary[j];
        }
        Array.Copy(permuteRe, real, Size);
        Array.Copy(permuteIm, imaginary, Size);

        for (int length = 2; length <= Size; length <<= 1)
        {
            int half = length >> 1;
            int tableStep = Size / length;
            for (int start = 0; start < Size; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    int twiddle = k * tableStep;
                    float wr = cosTable[twiddle];
                    float wi = sinTable[twiddle];
                    int evenIndex = start + k;
                    int oddIndex = evenIndex + half;

                    float evenRe = real[evenIndex];
                    float evenIm = imaginary[evenIndex];
                    float oddRe = real[oddIndex];
                    float oddIm = imaginary[oddIndex];

                    float tRe = oddRe * wr - oddIm * wi;
                    float tIm = oddRe * wi + oddIm * wr;

                    real[evenIndex] = evenRe + tRe;
                    imaginary[evenIndex] = evenIm + tIm;
                    real[oddIndex] = evenRe - tRe;
                    imaginary[oddIndex] = evenIm - tIm;
                }
            }
        }
    }
}
