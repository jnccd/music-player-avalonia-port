using System;
using System.Collections.Generic;

namespace MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

/// <summary>
/// Everything the wrapped analysis measured from one song file. Serialized into the durable analysis
/// cache (see <see cref="WrappedAudioCache"/>), so it is written with plain, stable types only.
/// <para>
/// The point of the audio half of the wrapped is that most of a private library exists nowhere online:
/// game and anime soundtracks, YouTube rips, Eurovision entries, private edits. So instead of asking a
/// database what a song is, the wrapped decodes the file and describes what it <i>sounds</i> like -
/// tempo, timbre, key, loudness shape - and then finds structure across the whole library from that.
/// </para>
/// </summary>
public sealed class AudioFeatures
{
    /// <summary>MFCC count the wrapped uses; explicit because the cache stores bare arrays.</summary>
    public const int CepstrumCount = 13;

    // ---- Loudness / energy ----

    /// <summary>Mean frame loudness, normalized to a common reference across the library (see <see cref="WrappedAudioAnalyzer"/>).</summary>
    public float LoudnessMean { get; set; }
    /// <summary>Loudness the song rarely falls below (10th percentile) - the quiet floor.</summary>
    public float LoudnessP10 { get; set; }
    /// <summary>Loudness the song rarely exceeds (90th percentile) - the loud ceiling.</summary>
    public float LoudnessP90 { get; set; }
    /// <summary>P90 - P10: how far the song travels between quiet and loud passages.</summary>
    public float DynamicRange { get; set; }
    /// <summary>Standard deviation of the frame loudness (sustained vs. punchy).</summary>
    public float LoudnessStd { get; set; }
    /// <summary>Fraction of the song that is actually loud (frames above 40% of the ceiling).</summary>
    public float LoudFraction { get; set; }

    /// <summary>
    /// How much the level swings between the loudest and the quietest second (in dB). High values are
    /// the hallmark of a dynamic, classically mixed master; near 0 dB means modern brick-wall loudness.
    /// </summary>
    public float CrestFactorDb { get; set; }

    // ---- Timbre / spectrum ----

    /// <summary>Spectral centre of mass in Hz: low = bass heavy, high = bright/airy.</summary>
    public float SpectralCentroidHz { get; set; }
    /// <summary>Frequency below which 85% of the spectral energy sits.</summary>
    public float SpectralRolloffHz { get; set; }
    /// <summary>0 = tonal (peaks), 1 = noise-like (flat). Separates noisy/distorted from clean material.</summary>
    public float SpectralFlatness { get; set; }
    /// <summary>Average per-frame spectral change: high for busy, dense material.</summary>
    public float SpectralFlux { get; set; }
    /// <summary>Share of the energy below ~250 Hz.</summary>
    public float BassRatio { get; set; }
    /// <summary>Share of the energy between ~250 Hz and ~4 kHz (the mix body).</summary>
    public float MidRatio { get; set; }
    /// <summary>Share of the energy above ~4 kHz (cymbals, air, hiss).</summary>
    public float TrebleRatio { get; set; }

    // ---- Rhythm ----

    /// <summary>Estimated tempo in BPM, folded into the 70..180 range (see <see cref="TempoEstimator"/>).</summary>
    public float Bpm { get; set; }
    /// <summary>Unfolded tempo the comb search actually found (e.g. 87 for a 174 BPM track).</summary>
    public float RawBpm { get; set; }
    /// <summary>0..1: how dominant the winning tempo hypothesis was over the runner-up.</summary>
    public float TempoConfidence { get; set; }
    /// <summary>Audible onsets per second - how busy the material is rhythmically.</summary>
    public float OnsetDensity { get; set; }
    /// <summary>Share of frames carrying an onset above the adaptive threshold.</summary>
    public float Percussiveness { get; set; }

    // ---- Key / harmony ----

    /// <summary>Estimated key, e.g. "A minor" (empty when the estimate had no confidence).</summary>
    public string KeyName { get; set; } = "";
    /// <summary>True for a minor estimate, false for major.</summary>
    public bool IsMinorKey { get; set; }
    /// <summary>Correlation of the chroma with the best Krumhansl profile (-1..1).</summary>
    public float KeyCorrelation { get; set; }
    /// <summary>Normalized pitch class histogram, C first.</summary>
    public float[] Chroma { get; set; } = new float[12];
    /// <summary>Share of the chroma energy that belongs to the estimated key's scale.</summary>
    public float KeyAdherence { get; set; }

    // ---- Timbre fingerprint ----

    /// <summary>Mean MFCCs; index 0 is skipped because it only carries the overall level.</summary>
    public float[] MfccMean { get; set; } = new float[CepstrumCount];
    /// <summary>Standard deviation of the MFCCs - how much the timbre moves over the song.</summary>
    public float[] MfccStd { get; set; } = new float[CepstrumCount];
    /// <summary>Mean MFCC deltas - how fast the timbre changes frame to frame.</summary>
    public float[] MfccDeltaMean { get; set; } = new float[CepstrumCount];

    // ---- Shape over time ----

    /// <summary>Compact peak loudness curve over the song, normalized to its own peak.</summary>
    public float[] LoudnessCurve { get; set; } = [];
    /// <summary>Coarse structural segments found by novelty segmentation.</summary>
    public List<AudioSection> Sections { get; set; } = [];
    /// <summary>The first <see cref="WrappedAudioAnalyzer.FrameFeatureCount"/> frames, flattened, 5 values each.</summary>
    public float[] FrameFeatures { get; set; } = [];

    /// <summary>Duration of the file.</summary>
    public double DurationSeconds { get; set; }
    /// <summary>How much of the file was actually analysed (everything except absurdly long files).</summary>
    public double AnalyzedSeconds { get; set; }
    /// <summary>Sample rate the analysis ran at.</summary>
    public int AnalysisSampleRate { get; set; }
}

/// <summary>
/// One structural segment of a song, found by novelty segmentation over the frame features. Deliberately
/// described in measurable terms rather than guessing "verse"/"chorus": with no online reference
/// involved, the honest statement is "a loud part", not "the chorus".
/// </summary>
public sealed class AudioSection
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    /// <summary>Mean loudness relative to the whole song (1.0 = the song's average).</summary>
    public float RelativeLoudness { get; set; }
    /// <summary>Spectral centroid relative to the whole song.</summary>
    public float RelativeBrightness { get; set; }
    /// <summary>Short readable description derived from the two relatives.</summary>
    public string Character { get; set; } = "";

    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}
