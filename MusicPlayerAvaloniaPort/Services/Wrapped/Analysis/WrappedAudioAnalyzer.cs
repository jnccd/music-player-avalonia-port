using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;

namespace MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

/// <summary>
/// Turns one song file into <see cref="AudioFeatures"/>.
/// <para>
/// One decode pass per file. The decoded signal is mixed to mono, filtered and streamed through the
/// analyzer in blocks: every analysis frame is transformed exactly once and its results are accumulated
/// into the loudness statistics, the chroma matrix, the onset signal, the mel log energies and the frame
/// feature matrix the structural segmentation works on. The whole decoded song is never held in memory,
/// so a three hour file costs the same as a three minute one.
/// </para>
/// <para>
/// Files are decoded at <see cref="AnalysisSampleRate"/> rather than their native rate: none of the
/// features used here (tempo, timbre, key, loudness) benefit from 44.1 kHz, and analysing at half the
/// rate halves both the decode and the FFT work - which is what makes a whole NAS library feasible in a
/// single run.
/// </para>
/// <para>
/// Everything is measured in two phases: the raw accumulation, and only then the loudness normalization.
/// The features the wrapped clusters on (tempo, timbre, key, spectral shape) are deliberately
/// level-independent, while loudness is normalized to a common reference afterwards - otherwise a
/// library's "quiet songs" would simply be the ones whose files happen to have been ripped more quietly.
/// </para>
/// </summary>
public sealed class WrappedAudioAnalyzer : IDisposable
{
    /// <summary>Sample rate every file is analysed at.</summary>
    public const int AnalysisSampleRate = 22050;

    /// <summary>FFT size of the analysis frames.</summary>
    public const int FftSize = 1024;

    /// <summary>Hop between frames: 25% of the window, i.e. ~86 frames per second at 22.05 kHz.</summary>
    public const int HopSize = 256;

    /// <summary>Mel bands of the filterbank (also the spectral flux resolution).</summary>
    public const int MelBands = 40;

    /// <summary>Loudness curve length stored per song.</summary>
    public const int LoudnessCurveLength = 200;

    /// <summary>Frames actually stored per song (the rest only feeds the statistics).</summary>
    public const int MaxStoredFrames = 6000;

    /// <summary>
    /// Slots the stored frames are downsampled to when the features are exported. Sixty-four slots still
    /// describe the song's shape over time (and give the clustering a view of its parts) while keeping one
    /// song's cache entry in the tens of kilobytes instead of hundreds.
    /// </summary>
    public const int FrameFeatureCount = 64;

    /// <summary>
    /// Length of one slot of the structural segmentation matrix. Half a second keeps the novelty curve
    /// sharp enough that a boundary lands where the music actually changes - at two seconds the averaging
    /// smoothed the curve so far that no boundary cleared the threshold - while still costing little.
    /// </summary>
    public const double SectionSlotSeconds = 0.5;

    /// <summary>Frame feature width used by the clustering and the structure detector.</summary>
    public const int FrameFeatureValues = 5;

    /// <summary>Longest prefix of a file that is analysed; only pathological files are longer.</summary>
    public const double MaxAnalyzedSeconds = 900;

    const int DecodeBlockFrames = 8192;
    const int FilterTaps = 32;
    const int WarmupFrames = 4;

    readonly Fft fft = new(FftSize);
    readonly MelFilterbank melFilterbank = new(MelBands, FftSize, AnalysisSampleRate);
    readonly float[] powerSpectrum;
    readonly float[] melEnergies;
    readonly float[] melEnergiesPrevious;

    readonly float[] lowPassTaps = new float[FilterTaps];
    readonly float[] filterHistory = new float[FilterTaps];
    int filterHistoryCount;
    float highPassPreviousInput;
    float highPassPreviousOutput;
    long decimationCounter;

    public WrappedAudioAnalyzer()
    {
        powerSpectrum = new float[fft.BinCount];
        melEnergies = new float[MelBands];
        melEnergiesPrevious = new float[MelBands];
        BuildLowPassTaps();
    }

    static readonly Lazy<MiniAudioEngine> LazyDecodeEngine = new(() => new MiniAudioEngine());

    /// <summary>
    /// A decoder engine purely for analysis. It never opens a playback device, so analysing thousands of
    /// files is headless and cannot interfere with what the player is doing on the audio hardware.
    /// </summary>
    static MiniAudioEngine DecodeEngine => LazyDecodeEngine.Value;

    /// <summary>
    /// Analyses a song file. Throws <see cref="IOException"/>/<see cref="InvalidDataException"/> when the
    /// file is unreadable or undecodable; the wrapped run records that per song instead of failing.
    /// </summary>
    public static AudioFeatures AnalyzeFile(string filePath, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var provider = new StreamDataProvider(DecodeEngine, stream, new ReadOptions { ReadTags = false });
        try
        {
            return AnalyzeProvider(provider, cancellationToken);
        }
        finally
        {
            provider.Dispose();
        }
    }

    /// <summary>Analyses an already opened decoder.</summary>
    public static AudioFeatures AnalyzeProvider(ISoundDataProvider provider, CancellationToken cancellationToken = default)
    {
        var analyzer = new WrappedAudioAnalyzer();
        return analyzer.Analyze(provider, cancellationToken);
    }

    /// <summary>
    /// Analyses mono samples that are already at <see cref="AnalysisSampleRate"/>. This is the entry point
    /// the verification harnesses use to push synthetic audio (for instance a click track with a known
    /// tempo) through exactly the same code path a decoded mp3 takes.
    /// </summary>
    public static AudioFeatures AnalyzeMonoSamples(ReadOnlySpan<float> monoSamples)
    {
        var analyzer = new WrappedAudioAnalyzer();
        return analyzer.AnalyzeMono(monoSamples);
    }

    /// <summary>Analyses a mono signal at the analysis sample rate without any decoder involved.</summary>
    public AudioFeatures AnalyzeMono(ReadOnlySpan<float> monoSamples)
    {
        var pass = new FeaturePass(MaxStoredFrames, this)
        {
            TotalSeconds = monoSamples.Length / (double)AnalysisSampleRate,
        };

        // No anti-alias filtering is needed (the signal already is at the analysis rate) but the 50 Hz
        // high pass still runs, so the results match the file path exactly.
        for (int i = 0; i < monoSamples.Length; i++)
            pass.AddSample(HighPass(monoSamples[i]));

        return pass.Finish();
    }

    /// <summary>Runs the analysis over a decoder.</summary>
    AudioFeatures Analyze(ISoundDataProvider provider, CancellationToken cancellationToken)
    {
        int channels = Math.Max(1, provider.FormatInfo?.ChannelCount ?? 2);
        int sourceRate = provider.FormatInfo?.SampleRate ?? 44100;
        if (sourceRate <= 0)
            sourceRate = 44100;

        double estimatedSeconds = sourceRate > 0 ? provider.Length / (double)sourceRate / channels : 0;
        if (estimatedSeconds <= 0 || estimatedSeconds > MaxAnalyzedSeconds * 4)
            estimatedSeconds = MaxAnalyzedSeconds;

        var pass = new FeaturePass(MaxStoredFrames, this)
        {
            TotalSeconds = Math.Min(estimatedSeconds, MaxAnalyzedSeconds),
        };

        // 44.1/48 kHz are both handled with a 2:1 decimation (see the class remark about the sample rate);
        // the anti-alias FIR runs before it, so no energy folds back into the analysis band.
        bool needsResampling = sourceRate != AnalysisSampleRate;
        int stride = needsResampling ? 2 : 1;
        int outputCount = needsResampling ? DecodeBlockFrames / channels / 2 + 1 : DecodeBlockFrames / channels + 1;
        var monoBlock = new float[Math.Max(1, outputCount)];
        var decodeBuffer = new float[DecodeBlockFrames];

        long sampleLimit = (long)(MaxAnalyzedSeconds * sourceRate) * channels;
        long samplesRead = 0;
        long decodedFrames = 0;

        while (samplesRead < sampleLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int wanted = (int)Math.Min(decodeBuffer.Length, sampleLimit - samplesRead);
            int read = provider.ReadBytes(decodeBuffer.AsSpan(0, wanted));
            if (read <= 0)
                break;
            samplesRead += read;

            int frameCount = read / channels;
            int produced = 0;
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sum = 0f;
                int baseIndex = frame * channels;
                for (int c = 0; c < channels; c++)
                    sum += decodeBuffer[baseIndex + c];

                float lowPassed = LowPass(sum / channels);
                decimationCounter++;
                if (decimationCounter % stride != 0)
                    continue;

                monoBlock[produced++] = HighPass(lowPassed);
            }

            decodedFrames += frameCount;
            pass.AddSamples(monoBlock.AsSpan(0, produced));

            if (pass.TotalSeconds > 0)
                ReportProgress(decodedFrames / (double)sourceRate / pass.TotalSeconds);
        }

        CurrentFileProgress = 1.0;
        return pass.Finish();
    }

    /// <summary>How far (0..1) the file currently being analysed is.</summary>
    public double CurrentFileProgress { get; private set; }

    void ReportProgress(double progress) => CurrentFileProgress = Math.Clamp(progress, 0.0, 1.0);

    /// <summary>Estimated duration of the file currently being analysed (used for time estimates).</summary>
    public double TotalSeconds { get; private set; }

    // -------------------------------------------------------------------------------------------------
    //  The streaming accumulation.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Accumulates everything one file's frames produce. Kept as a nested class so the analyzer instance
    /// (FFT tables, filter state) can be reused while all per-file state lives here.
    /// </summary>
    sealed class FeaturePass
    {
        readonly List<float> flux = [];
        readonly List<float> rms = [];
        readonly List<float> centroid = [];
        readonly List<float> rolloff = [];
        readonly List<float> flatness = [];
        readonly List<float> zeroCrossing = [];
        readonly List<float> bassRatio = [];
        readonly List<float> trebleRatio = [];
        readonly List<float> frameFeatures = [];

        // Mel log energies are needed twice (cepstral statistics and the frame delta features), so the
        // whole list is kept; 40 floats per frame is a few hundred kB for a long song.
        readonly List<float[]> logMel = [];
        // Chroma is normalized per frame while accumulating, so every frame contributes equally.
        readonly double[] chroma = new double[12];

        readonly WrappedAudioAnalyzer analyzer;
        float[] analysisBuffer = new float[DecodeBlockFrames];
        int analysisBufferCount;
        int analysisBufferPosition;
        long frameIndex;

        public double TotalSeconds { get; set; }

        public FeaturePass(int maxStoredFrames, WrappedAudioAnalyzer analyzer)
        {
            MaxStoredFrames = maxStoredFrames;
            this.analyzer = analyzer;
        }

        int MaxStoredFrames { get; }

        public void AddSamples(ReadOnlySpan<float> samples)
        {
            foreach (float sample in samples)
                AddSample(sample);
        }

        /// <summary>Queues one sample of the (already resampled and filtered) analysis signal.</summary>
        public void AddSample(float sample)
        {
            if (analysisBufferCount == analysisBuffer.Length)
            {
                var grown = new float[analysisBuffer.Length * 2];
                Array.Copy(analysisBuffer, grown, analysisBuffer.Length);
                analysisBuffer = grown;
            }
            analysisBuffer[analysisBufferCount++] = sample;

            while (analysisBufferCount - analysisBufferPosition >= FftSize)
            {
                AnalyzeFrame(analysisBuffer.AsSpan(analysisBufferPosition, FftSize));
                analysisBufferPosition += HopSize;
            }

            if (analysisBufferPosition > 0)
            {
                int remaining = analysisBufferCount - analysisBufferPosition;
                if (remaining > 0)
                    Array.Copy(analysisBuffer, analysisBufferPosition, analysisBuffer, 0, remaining);
                analysisBufferCount = remaining;
                analysisBufferPosition = 0;
            }
        }

        void AnalyzeFrame(ReadOnlySpan<float> frame)
        {
            long index = frameIndex++;

            float sumSquares = 0f;
            int crossings = 0;
            float previous = frame[0];
            for (int i = 0; i < frame.Length; i++)
            {
                float value = frame[i];
                sumSquares += value * value;
                if ((value >= 0f) != (previous >= 0f))
                    crossings++;
                previous = value;
            }

            float frameRms = MathF.Sqrt(sumSquares / frame.Length);
            float frameZeroCrossing = crossings / (float)frame.Length;

            analyzer.fft.PowerSpectrum(frame, analyzer.powerSpectrum);

            int binCount = analyzer.fft.BinCount;
            double binWidth = AnalysisSampleRate / 2.0 / (binCount - 1);
            double totalEnergy = 0.0;
            double weightedFrequency = 0.0;
            double logSum = 0.0;
            double bass = 0.0, mid = 0.0, treble = 0.0;
            for (int bin = 0; bin < binCount; bin++)
            {
                float power = analyzer.powerSpectrum[bin];
                totalEnergy += power;
                double frequency = bin * binWidth;
                weightedFrequency += power * frequency;
                logSum += Math.Log(power + 1e-12);
                if (frequency < 250.0)
                    bass += power;
                else if (frequency < 4000.0)
                    mid += power;
                else
                    treble += power;
            }
            if (totalEnergy <= 1e-12)
                totalEnergy = 1e-12;

            float frameCentroid = (float)(weightedFrequency / totalEnergy);
            float frameFlatness = (float)(Math.Exp(logSum / binCount) / (totalEnergy / binCount + 1e-12));

            double rolloffTarget = totalEnergy * 0.85;
            double running = 0.0;
            int rolloffBin = binCount - 1;
            for (int bin = 0; bin < binCount; bin++)
            {
                running += analyzer.powerSpectrum[bin];
                if (running >= rolloffTarget)
                {
                    rolloffBin = bin;
                    break;
                }
            }
            float frameRolloff = (float)(rolloffBin * binWidth);

            analyzer.melFilterbank.Apply(analyzer.powerSpectrum, analyzer.melEnergies);

            float frameFlux = 0f;
            var logMelFrame = new float[MelBands];
            for (int band = 0; band < MelBands; band++)
            {
                float energy = analyzer.melEnergies[band];
                float difference = energy - analyzer.melEnergiesPrevious[band];
                if (difference > 0f)
                    frameFlux += difference;
                analyzer.melEnergiesPrevious[band] = energy;
                logMelFrame[band] = MathF.Log(Math.Max(energy, 1e-10f));
            }

            // Chroma: fold the spectrum onto the 12 pitch classes, discarding the very low bins whose
            // harmonics would smear across several classes. Normalized per frame so loud frames do not
            // dominate the key estimate.
            Span<double> frameChroma = stackalloc double[12];
            for (int bin = 1; bin < binCount; bin++)
            {
                double frequency = bin * binWidth;
                if (frequency < 55.0)
                    continue;
                double midi = 69.0 + 12.0 * Math.Log2(frequency / 440.0);
                int pitchClass = (int)Math.Round(midi) % 12;
                if (pitchClass < 0)
                    pitchClass += 12;
                frameChroma[pitchClass] += analyzer.powerSpectrum[bin];
            }
            double frameChromaTotal = 0.0;
            for (int pitchClass = 0; pitchClass < 12; pitchClass++)
                frameChromaTotal += frameChroma[pitchClass];

            bool harmonic = frameChromaTotal > 1e-6 && frameFlatness < 0.6;

            // The first frames carry the filter warm-up transient; they are counted in the frame matrix
            // (so the shape of the song stays aligned) but excluded from the statistics.
            bool inStatistics = index >= WarmupFrames;

            if (inStatistics)
            {
                rms.Add(frameRms);
                centroid.Add(frameCentroid);
                rolloff.Add(frameRolloff);
                flatness.Add(frameFlatness);
                zeroCrossing.Add(frameZeroCrossing);
                bassRatio.Add((float)(bass / totalEnergy));
                trebleRatio.Add((float)(treble / totalEnergy));
                flux.Add(frameFlux);
                logMel.Add(logMelFrame);
                if (harmonic)
                {
                    for (int pitchClass = 0; pitchClass < 12; pitchClass++)
                        chroma[pitchClass] += frameChroma[pitchClass] / frameChromaTotal;
                }
            }

            // Frame matrix: the loudness entry is written raw here and normalized once the file's
            // reference level is known (see Finish).
            if (frameFeatures.Count < MaxStoredFrames * FrameFeatureValues)
            {
                frameFeatures.Add(frameRms);
                frameFeatures.Add(frameCentroid);
                frameFeatures.Add(frameRolloff / 1000f);
                frameFeatures.Add(frameFlatness * 100f);
                frameFeatures.Add(frameFlux);
                StoredFrameCount++;
            }

            // The structural segmentation accumulates over EVERY frame, not just the stored ones, so it
            // covers the whole song. Keeping only the first MaxStoredFrames frames (about seventy seconds)
            // and segmenting those made the section times wrong for anything longer than that.
            int slot = (int)(index * HopSize / (SectionSlotSeconds * AnalysisSampleRate));
            if (slot >= 0)
            {
                if (slot >= sectionSlotCounts.Length)
                {
                    int newLength = Math.Max(slot + 1, sectionSlotCounts.Length * 2);
                    Array.Resize(ref sectionSlotCounts, newLength);
                    Array.Resize(ref sectionSlotSums, newLength * FrameFeatureValues);
                }
                int target = slot * FrameFeatureValues;
                sectionSlotSums[target] += frameRms;
                sectionSlotSums[target + 1] += frameCentroid;
                sectionSlotSums[target + 2] += frameRolloff / 1000f;
                sectionSlotSums[target + 3] += frameFlatness * 100f;
                sectionSlotSums[target + 4] += frameFlux;
                sectionSlotCounts[slot]++;
            }
        }

        /// <summary>Per-slot sums and counts of the structural segmentation matrix (see <see cref="BuildSectionMatrix"/>).</summary>
        int[] sectionSlotCounts = new int[256];
        float[] sectionSlotSums = new float[256 * FrameFeatureValues];

        /// <summary>Total analysis frames the pass saw - the true length of the analysis, in frames.</summary>
        public long FrameCount => frameIndex;

        public int StoredFrameCount { get; private set; }

        public AudioFeatures Finish()
        {
            var features = new AudioFeatures
            {
                AnalysisSampleRate = AnalysisSampleRate,
                DurationSeconds = TotalSeconds,
                AnalyzedSeconds = frameIndex * HopSize / (double)AnalysisSampleRate,
            };

            int frameCount = rms.Count;
            if (frameCount == 0 || frameFeatures.Count < FrameFeatureValues)
                return features;

            double framesPerSecond = AnalysisSampleRate / (double)HopSize;

            // ---- Loudness normalization ----
            // Files differ wildly in overall level; without normalizing, "loudness" would measure how the
            // file was ripped rather than how the music is shaped.
            var sortedRms = rms.ToArray();
            Array.Sort(sortedRms);
            float reference = Math.Max(Percentile(sortedRms, 0.90f), 1e-4f);
            float gain = Math.Clamp(0.2f / reference, 0.05f, 100f);

            float rmsMean = Mean(sortedRms);
            features.LoudnessMean = rmsMean * gain;
            features.LoudnessP10 = Percentile(sortedRms, 0.10f) * gain;
            features.LoudnessP90 = reference * gain;
            features.DynamicRange = features.LoudnessP90 - features.LoudnessP10;
            features.LoudnessStd = StandardDeviation(sortedRms, rmsMean) * gain;
            features.CrestFactorDb = Db(Percentile(sortedRms, 0.95f) / Math.Max(Percentile(sortedRms, 0.05f), 1e-6f));

            float ceiling = Math.Max(features.LoudnessP90, 1e-6f);
            int loudFrames = 0;
            foreach (float value in sortedRms)
                if (value * gain > 0.4f * ceiling)
                    loudFrames++;
            features.LoudFraction = loudFrames / (float)sortedRms.Length;

            // ---- Spectral statistics ----
            features.SpectralCentroidHz = Mean(centroid);
            features.SpectralRolloffHz = Mean(rolloff);
            features.SpectralFlatness = Mean(flatness);
            features.BassRatio = Mean(bassRatio);
            features.TrebleRatio = Mean(trebleRatio);
            features.MidRatio = Math.Max(0f, 1f - features.BassRatio - features.TrebleRatio);
            features.SpectralFlux = Mean(flux);

            // ---- Rhythm ----
            var fluxArray = flux.ToArray();
            var onsetStrength = TempoEstimator.OnsetStrength(fluxArray, out float onsetDensity, framesPerSecond);
            var tempo = TempoEstimator.Estimate(onsetStrength, framesPerSecond);
            features.Bpm = tempo.Bpm;
            features.RawBpm = tempo.RawBpm;
            features.TempoConfidence = tempo.Confidence;
            features.OnsetDensity = onsetDensity;

            int percussive = 0;
            float onsetMean = Mean(onsetStrength);
            foreach (float value in onsetStrength)
                if (value > onsetMean)
                    percussive++;
            features.Percussiveness = onsetStrength.Length > 0 ? percussive / (float)onsetStrength.Length : 0f;

            // ---- Key ----
            var chromaVector = new float[12];
            double chromaTotal = 0.0;
            for (int i = 0; i < 12; i++)
                chromaTotal += chroma[i];
            if (chromaTotal > 1e-9)
                for (int i = 0; i < 12; i++)
                    chromaVector[i] = (float)(chroma[i] / chromaTotal);
            features.Chroma = chromaVector;
            var key = KeyEstimator.Estimate(chromaVector);
            features.KeyName = key.KeyName;
            features.IsMinorKey = key.IsMinor;
            features.KeyCorrelation = key.Correlation;
            features.KeyAdherence = key.Adherence;

            // ---- Cepstral (timbre) statistics ----
            FillCepstralStatistics(features);

            // ---- Shape ----
            FillLoudnessCurve(features, sortedRms, gain);
            FillFrameFeatures(features, gain);
            features.FrameSampleCount = StoredFrameCount;
            features.SectionFeatures = BuildSectionMatrix(gain);
            features.SectionSampleCount = (int)frameIndex;
            features.Sections = DetectSections(features, TotalSeconds);

            return features;
        }
        /// <summary>
        /// MFCC mean / standard deviation / frame-delta mean. The deltas are what distinguishes a
        /// constantly shifting arrangement from a static loop, which tempo and mean timbre alone cannot
        /// separate.
        /// </summary>
        void FillCepstralStatistics(AudioFeatures features)
        {
            int frames = logMel.Count;
            if (frames == 0)
                return;

            var sums = new double[AudioFeatures.CepstrumCount];
            var squares = new double[AudioFeatures.CepstrumCount];
            var deltaSums = new double[AudioFeatures.CepstrumCount];
            Span<float> cepstrum = stackalloc float[AudioFeatures.CepstrumCount];
            Span<float> previous = stackalloc float[AudioFeatures.CepstrumCount];
            bool hasPrevious = false;
            int counted = 0;

            foreach (var melFrame in logMel)
            {
                // LogMelToCepstrum expects raw energies; the stored values are already logarithmic, so the
                // DCT is applied here directly instead of through the helper.
                DctFromLogMel(melFrame, cepstrum);

                for (int c = 0; c < cepstrum.Length; c++)
                {
                    sums[c] += cepstrum[c];
                    squares[c] += (double)cepstrum[c] * cepstrum[c];
                    if (hasPrevious)
                        deltaSums[c] += Math.Abs(cepstrum[c] - previous[c]);
                }
                cepstrum.CopyTo(previous);
                hasPrevious = true;
                counted++;
            }

            if (counted == 0)
                return;

            for (int c = 0; c < AudioFeatures.CepstrumCount; c++)
            {
                double mean = sums[c] / counted;
                features.MfccMean[c] = (float)mean;
                features.MfccStd[c] = (float)Math.Sqrt(Math.Max(0.0, squares[c] / counted - mean * mean));
                features.MfccDeltaMean[c] = (float)(deltaSums[c] / Math.Max(1, counted - 1));
            }
        }

        static void DctFromLogMel(float[] logMelFrame, Span<float> cepstrum)
        {
            int bands = logMelFrame.Length;
            int count = Math.Min(cepstrum.Length, bands);
            double scale = Math.PI / bands;
            for (int c = 0; c < count; c++)
            {
                double sum = 0.0;
                for (int b = 0; b < bands; b++)
                    sum += logMelFrame[b] * Math.Cos(scale * (b + 0.5) * c);
                cepstrum[c] = (float)(sum * Math.Sqrt(2.0 / bands));
            }
        }

        void FillLoudnessCurve(AudioFeatures features, float[] sortedRms, float gain)
        {
            var curve = new float[LoudnessCurveLength];
            var series = rms.ToArray();
            if (series.Length == 0)
            {
                features.LoudnessCurve = curve;
                return;
            }

            for (int bucket = 0; bucket < LoudnessCurveLength; bucket++)
            {
                int from = (int)((long)bucket * series.Length / LoudnessCurveLength);
                int to = (int)((long)(bucket + 1) * series.Length / LoudnessCurveLength);
                if (to <= from)
                    to = Math.Min(series.Length, from + 1);

                float max = 0f;
                for (int i = from; i < to; i++)
                    if (series[i] > max)
                        max = series[i];
                curve[bucket] = max * gain;
            }

            float peak = 0f;
            foreach (float value in curve)
                if (value > peak)
                    peak = value;
            if (peak > 1e-6f)
                for (int i = 0; i < curve.Length; i++)
                    curve[i] /= peak;

            features.LoudnessCurve = curve;
            _ = sortedRms;
        }

        /// <summary>
        /// Applies the loudness gain to the recorded frames and downsamples them to
        /// <see cref="FrameFeatureCount"/> slots. Downsampling (rather than keeping every recorded frame) is
        /// what keeps one song's cache entry small: at ~6000 frames per song the serialized matrix alone was
        /// ~300 kB, which made a whole library's cache about a gigabyte and therefore impossible to write.
        /// The sections are derived from this same sampled matrix, so nothing that is displayed is lost.
        /// </summary>
        void FillFrameFeatures(AudioFeatures features, float gain)
        {
            var compact = new float[FrameFeatureCount * FrameFeatureValues];
            if (StoredFrameCount == 0)
            {
                features.FrameFeatures = compact;
                return;
            }

            for (int slot = 0; slot < FrameFeatureCount; slot++)
            {
                int source = (int)((long)slot * StoredFrameCount / FrameFeatureCount);
                if (source >= StoredFrameCount)
                    source = StoredFrameCount - 1;

                int target = slot * FrameFeatureValues;
                int origin = source * FrameFeatureValues;
                compact[target] = frameFeatures[origin] * gain;
                for (int value = 1; value < FrameFeatureValues; value++)
                    compact[target + value] = frameFeatures[origin + value];
            }
            features.FrameFeatures = compact;
        }

        /// <summary>
        /// Averages every frame of the song into <see cref="SectionSlotSeconds"/> long slots for the
        /// structural segmentation. Accumulated while the song streams (see <see cref="AnalyzeFrame"/>), so
        /// it covers the whole file - unlike the frames kept for the shape export, which stop after
        /// <see cref="MaxStoredFrames"/>. Averaging (rather than picking frames) keeps every moment
        /// represented, so a short quiet passage a sampler would step over still shows up as a dip.
        /// </summary>
        float[] BuildSectionMatrix(float gain)
        {
            if (sectionSlotCounts.Length == 0)
                return [];

            int usedSlots = 0;
            for (int slot = 0; slot < sectionSlotCounts.Length; slot++)
                if (sectionSlotCounts[slot] > 0)
                    usedSlots = slot + 1;

            if (usedSlots == 0)
                return [];

            var matrix = new float[usedSlots * FrameFeatureValues];
            for (int slot = 0; slot < usedSlots; slot++)
            {
                int count = Math.Max(1, sectionSlotCounts[slot]);
                int target = slot * FrameFeatureValues;
                for (int value = 0; value < FrameFeatureValues; value++)
                {
                    float sum = sectionSlotSums[target + value];
                    // Only loudness is gain dependent (value 0); the rest are level invariant.
                    matrix[target + value] = value == 0 ? sum * gain / count : sum / count;
                }
            }

            return matrix;
        }

        // ---- statistics helpers ----

        static float Percentile(float[] sortedValues, float fraction)
        {
            if (sortedValues.Length == 0)
                return 0f;
            int index = (int)Math.Round((sortedValues.Length - 1) * fraction);
            return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
        }

        static float Mean(IReadOnlyList<float> values)
        {
            if (values.Count == 0)
                return 0f;
            double sum = 0.0;
            for (int i = 0; i < values.Count; i++)
                sum += values[i];
            return (float)(sum / values.Count);
        }

        static float Mean(float[] values)
        {
            if (values.Length == 0)
                return 0f;
            double sum = 0.0;
            for (int i = 0; i < values.Length; i++)
                sum += values[i];
            return (float)(sum / values.Length);
        }

        static float Mean(ReadOnlySpan<float> values)
        {
            if (values.Length == 0)
                return 0f;
            double sum = 0.0;
            for (int i = 0; i < values.Length; i++)
                sum += values[i];
            return (float)(sum / values.Length);
        }

        static float StandardDeviation(float[] values, float mean)
        {
            if (values.Length == 0)
                return 0f;
            double sum = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                double difference = values[i] - mean;
                sum += difference * difference;
            }
            return (float)Math.Sqrt(sum / values.Length);
        }

        static float Db(float ratio) => ratio <= 1e-9f ? 0f : (float)(20.0 * Math.Log10(ratio));
    }

    // -------------------------------------------------------------------------------------------------
    //  Filters.
    // -------------------------------------------------------------------------------------------------

    void BuildLowPassTaps()
    {
        // Windowed sinc, cutoff slightly below the new Nyquist frequency so nothing folds back into the
        // analysis band when the signal is decimated.
        const double cutoff = 0.45; // fraction of the new Nyquist frequency
        double sum = 0.0;
        for (int i = 0; i < FilterTaps; i++)
        {
            double x = i - (FilterTaps - 1) / 2.0;
            double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
            double hann = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FilterTaps - 1)));
            lowPassTaps[i] = (float)(sinc * hann);
            sum += lowPassTaps[i];
        }
        for (int i = 0; i < FilterTaps; i++)
            lowPassTaps[i] = (float)(lowPassTaps[i] / sum);
    }

    float LowPass(float sample)
    {
        if (filterHistoryCount < FilterTaps)
        {
            filterHistory[filterHistoryCount++] = sample;
            return sample;
        }

        for (int i = 0; i < FilterTaps - 1; i++)
            filterHistory[i] = filterHistory[i + 1];
        filterHistory[FilterTaps - 1] = sample;

        double sum = 0.0;
        for (int i = 0; i < FilterTaps; i++)
            sum += filterHistory[i] * lowPassTaps[i];
        return (float)sum;
    }

    float HighPass(float sample)
    {
        // One pole high pass at ~50 Hz: removes DC and rumble, which would otherwise dominate the bass
        // ratio and smear the chroma.
        const float r = 0.9857f; // exp(-2*pi*50/22050)
        float output = r * (highPassPreviousOutput + sample - highPassPreviousInput);
        highPassPreviousInput = sample;
        highPassPreviousOutput = output;
        return output;
    }

    public void Dispose()
    {
        // The decoder engine is process wide and intentionally not disposed per analyzer; nothing else is
        // unmanaged here. The method exists so callers can use `using` uniformly.
    }

    // ---------------------------------------------------------------------------------------------
    //  Structural segmentation (also used to rebuild sections from the analysis cache).
    // ---------------------------------------------------------------------------------------------

/// <summary>
/// Finds the structural segments by novelty segmentation over the sampled frame matrix: adjacent
/// windows are compared, a boundary sits where the sound changes most, and the resulting segments
/// are then described in measurable terms (loud/quiet, bright/dark). Deliberately not
/// "verse"/"chorus" - with no online reference involved that would be a guess.
/// <para>
/// This runs on <see cref="AudioFeatures.SectionFeatures"/> - the song's frames averaged into
/// <see cref="SectionSlotSeconds"/> long slots - so it has both the resolution to place a boundary where
/// the music changes and the same input on a re-run, which is what lets a song loaded from the analysis
/// cache produce exactly the sections a fresh analysis produced.
/// </para>
/// </summary>
    internal static List<AudioSection> DetectSections(AudioFeatures features, double totalSeconds)
{
    var sections = new List<AudioSection>();
    var data = features.SectionFeatures;
    int frames = data.Length / FrameFeatureValues;
    const int windowRadius = 4;
    if (frames < windowRadius * 2 + 2)
        return sections;

    // Novelty: cosine-ish distance between the means of the frames left and right of a position.
    var novelty = new float[frames];
    for (int center = windowRadius; center < frames - windowRadius; center++)
    {
        double distance = 0.0;
        double normLeft = 0.0;
        double normRight = 0.0;
        for (int radius = 1; radius <= windowRadius; radius++)
        {
            distance += Distance(data, center - radius, center + radius - 1);
            normLeft += Norm(data, center - radius);
            normRight += Norm(data, center + radius - 1);
        }
        double denominator = Math.Sqrt(normLeft * normRight);
        novelty[center] = denominator > 1e-9 ? (float)(distance / denominator) : 0f;
    }

    float noveltyMean = MeanOf(novelty);
    float noveltyStd = StandardDeviationOf(novelty, noveltyMean);
    // A little above the average change: demanding a full standard deviation left whole songs (and the
    // smoother stretches of the others) with no boundary at all.
    float threshold = noveltyMean + 0.5f * noveltyStd;

    var candidates = new List<int>();
    for (int i = 1; i < frames - 1; i++)
        if (novelty[i] > threshold && novelty[i] >= novelty[i - 1] && novelty[i] >= novelty[i + 1])
            candidates.Add(i);
    candidates.Sort((left, right) => novelty[right].CompareTo(novelty[left]));

    // The slots stand for the whole song, so the slot length is the time scale. This must not be derived
    // from SectionSampleCount: that counts analysis frames (~11 ms each), not slots, and mixing the two
    // squashed every section to a few seconds at the start of the song.
    double secondsPerFrame = SectionSlotSeconds;

    // At least fifteen seconds between two boundaries, so the segments describe the song's form instead of
    // picking up a single fill or a drum break. `IgnoreEdgeSeconds` keeps the routine from spending its
    // boundaries on the song's own start and end, which are always a change but never a segment.
    const double IgnoreEdgeSeconds = 8.0;
    int edgeSlots = (int)Math.Round(IgnoreEdgeSeconds / Math.Max(1e-6, secondsPerFrame));
    var interior = candidates.Where(index => index >= edgeSlots && index <= frames - 1 - edgeSlots).ToList();
    if (interior.Count == 0)
        interior = candidates;

    int minimumGap = Math.Max(1, (int)Math.Round(15.0 / Math.Max(1e-6, secondsPerFrame)));
    minimumGap = Math.Min(minimumGap, Math.Max(1, frames / 4));

    double songEndSeconds = totalSeconds > 0 ? Math.Min(totalSeconds, DurationFromSlots(frames)) : DurationFromSlots(frames);

    // The last frame starts one window before the end of the decoded signal, so scaling by
    // AnalyzedSeconds would push the final section past the end of the song. The file's own
    // duration is the bound the display needs.

    var boundaries = new List<int>();
    foreach (int candidate in interior)
    {
        if (boundaries.Count >= 7)
            break;
        bool tooClose = false;
        foreach (int existing in boundaries)
            if (Math.Abs(existing - candidate) < minimumGap)
            {
                tooClose = true;
                break;
            }
        if (!tooClose)
            boundaries.Add(candidate);
    }
    boundaries.Sort();

    var edges = new List<int> { 0 };
    edges.AddRange(boundaries);
    edges.Add(frames - 1);

    double overallLoudness = 0.0;
    double overallBrightness = 0.0;
    for (int frame = 0; frame < frames; frame++)
    {
        overallLoudness += data[frame * FrameFeatureValues];
        overallBrightness += data[frame * FrameFeatureValues + 1];
    }
    overallLoudness /= frames;
    overallBrightness /= frames;

    for (int i = 0; i < edges.Count - 1; i++)
    {
        int from = edges[i];
        int to = edges[i + 1];
        if (to <= from)
            continue;

        double loudness = 0.0;
        double brightness = 0.0;
        for (int frame = from; frame <= to; frame++)
        {
            loudness += data[frame * FrameFeatureValues];
            brightness += data[frame * FrameFeatureValues + 1];
        }
        int count = to - from + 1;
        double relativeLoudness = overallLoudness > 1e-9 ? loudness / count / overallLoudness : 1.0;
        double relativeBrightness = overallBrightness > 1e-9 ? brightness / count / overallBrightness : 1.0;

        double startSeconds = Math.Min(Math.Round(from * secondsPerFrame, 1), songEndSeconds);
        double endSeconds = Math.Min(Math.Round(to * secondsPerFrame, 1), songEndSeconds);
        if (endSeconds - startSeconds < 1.0)
            continue; // clamped away by the end of the song - not a segment the UI should show

        sections.Add(new AudioSection
        {
            StartSeconds = startSeconds,
            EndSeconds = endSeconds,
            RelativeLoudness = (float)relativeLoudness,
            RelativeBrightness = (float)relativeBrightness,
            Character = DescribeSection(relativeLoudness, relativeBrightness),
        });
    }

    return sections;
}

/// <summary>How long the sampled slots span, in seconds.</summary>
static double DurationFromSlots(int frames) => Math.Max(0, frames - 1) * SectionSlotSeconds;

/// <summary>Mean of a signal (used by the structural segmentation).</summary>
static float MeanOf(float[] values)
{
    if (values.Length == 0)
        return 0f;
    double sum = 0.0;
    for (int i = 0; i < values.Length; i++)
        sum += values[i];
    return (float)(sum / values.Length);
}

/// <summary>Standard deviation of a signal around a known mean.</summary>
static float StandardDeviationOf(float[] values, float mean)
{
    if (values.Length == 0)
        return 0f;
    double sum = 0.0;
    for (int i = 0; i < values.Length; i++)
    {
        double difference = values[i] - mean;
        sum += difference * difference;
    }
    return (float)Math.Sqrt(sum / values.Length);
}
static string DescribeSection(double relativeLoudness, double relativeBrightness)
{
    string loudness = relativeLoudness switch
    {
        >= 1.25 => "loud",
        >= 1.05 => "full",
        >= 0.85 => "steady",
        >= 0.6 => "held back",
        _ => "quiet",
    };
    string brightness = relativeBrightness switch
    {
        >= 1.25 => "bright",
        >= 1.08 => "open",
        >= 0.92 => "balanced",
        >= 0.75 => "warm",
        _ => "dark",
    };
    return $"{loudness}, {brightness}";
}

static double Distance(float[] data, int firstIndex, int secondIndex)
{
    int first = firstIndex * FrameFeatureValues;
    int second = secondIndex * FrameFeatureValues;
    double sum = 0.0;
    for (int i = 0; i < FrameFeatureValues; i++)
    {
        double difference = data[first + i] - data[second + i];
        sum += difference * difference;
    }
    return Math.Sqrt(sum);
}

static double Norm(float[] data, int index)
{
    int offset = index * FrameFeatureValues;
    double sum = 0.0;
    for (int i = 0; i < FrameFeatureValues; i++)
        sum += (double)data[offset + i] * data[offset + i];
    return Math.Sqrt(sum);
}
}
