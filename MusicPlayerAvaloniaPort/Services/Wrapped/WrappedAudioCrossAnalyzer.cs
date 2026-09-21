using System;
using System.Collections.Generic;
using System.Linq;
using MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// A song as the audio cross-analysis sees it: its features plus what the listener did with it.
/// </summary>
public sealed class WrappedAnalysedSong
{
    public Guid SongId { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public string FilePath { get; set; } = "";
    public AudioFeatures Features { get; set; } = new();

    /// <summary>Lifetime counters of the row (not limited to the wrapped period).</summary>
    public int TotalLikes { get; set; }
    public int TotalDislikes { get; set; }
    public float Score { get; set; }
    public DateTimeOffset? DateAdded { get; set; }

    /// <summary>Plays inside the wrapped period.</summary>
    public int PeriodPlays { get; set; }
    /// <summary>Plays inside the period that were upvoted / downvoted.</summary>
    public int PeriodUpvotes { get; set; }
    public int PeriodDownvotes { get; set; }
}

/// <summary>
/// Everything the wrapped says about how the music <i>sounds</i>, as opposed to how often it was played.
/// <para>
/// This is the half that has to work without any online database: the songs are grouped into "sound
/// worlds" by clustering their measured features, the groups are labelled from their own statistics
/// (never from a genre somebody guessed), and the listening history is then projected onto those groups -
/// which turns "you played 4 000 songs" into "this is the sound you actually reach for, and this is the
/// one you own but ignore".
/// </para>
/// </summary>
public static class WrappedAudioCrossAnalyzer
{
    /// <summary>
    /// The features the clustering runs on, in order. Kept as an explicit list so the labels, the
    /// standardization and the cluster descriptions can never drift apart from the vectors.
    /// </summary>
    public static readonly string[] FeatureNames =
    [
        "tempo",
        "loudness",
        "dynamic range",
        "transient punch",
        "brightness",
        "spectral rolloff",
        "noisiness",
        "spectral motion",
        "bass weight",
        "treble weight",
        "onset density",
        "percussiveness",
        "chroma stability",
        "timbre (low cepstrum)",
        "timbre (mid cepstrum)",
        "timbre (high cepstrum)",
        "timbre movement",
        "section contrast",
    ];

    public const int FeatureCount = 18;

    /// <summary>Fills the report's audio sections. Songs with no features are ignored throughout.</summary>
    public static void Analyze(
        WrappedReport report,
        IReadOnlyList<WrappedAnalysedSong> songs,
        int periodPlayTotal,
        IReadOnlyList<IReadOnlyList<Guid>> periodPlaySequences)
    {
        var analysed = songs.Where(song => song.Features.AnalyzedSeconds > 0).ToList();
        report.AudioProfile = BuildProfile(analysed);
        if (analysed.Count < 8)
        {
            report.Notes.Add("The sound analysis needs more analysed songs to group them; run the audio analysis for the library.");
            BuildSignatureSongs(report, analysed);
            return;
        }

        report.Keys = BuildKeyCounts(analysed);
        var vectors = BuildVectors(analysed, out double[] means, out double[] deviations);

        // Cluster on principal components, not on the raw features: five of the eighteen measure essentially
        // the same thing (how bright/noisy the mix is), which made them decide the split between them.
        var projected = ProjectToPrincipalComponents(vectors, out double[] explainedVariance);
        var clusters = Cluster(projected);

        report.SoundClusters = BuildClusters(analysed, vectors, clusters, means, deviations, periodPlayTotal);
        report.SoundClusters.ForEach(cluster => cluster.ComponentsUsed = projected[0].Length);
        report.Notes.Add($"The sound grouping ran on {projected[0].Length} independent directions of the measured sound " +
                         $"(covering {100.0 * explainedVariance.Sum():0}% of the differences between your songs), so one aspect of the " +
                         "sound cannot decide the whole split on its own.");
        BuildSignatureSongs(report, analysed);
        report.Highlights.AddRange(BuildClusterHighlights(report, periodPlayTotal));
    }

    /// <summary>The vectors the clustering actually ran on, exposed for the diagnostic.</summary>
    static double[][] BuildProjectionForDiagnostics(double[][] vectors, out double[] explained)
    {
        return ProjectToPrincipalComponents(vectors, out explained);
    }

    // ---------------------------------------------------------------------------------------------
    //  Profile of the whole analysed library
    // ---------------------------------------------------------------------------------------------

    static WrappedAudioProfile BuildProfile(IReadOnlyList<WrappedAnalysedSong> analysed)
    {
        var profile = new WrappedAudioProfile { AnalysedSongs = analysed.Count };
        if (analysed.Count == 0)
            return profile;

        profile.AverageBpm = analysed.Average(song => song.Features.Bpm);
        var bpms = analysed.Select(song => song.Features.Bpm).OrderBy(bpm => bpm).ToList();
        profile.MedianBpm = bpms[bpms.Count / 2];
        profile.AverageLoudness = analysed.Average(song => song.Features.LoudnessMean);
        profile.AverageDynamicRange = analysed.Average(song => song.Features.DynamicRange);
        profile.AverageCrestFactorDb = analysed.Average(song => song.Features.CrestFactorDb);
        profile.AverageBrightnessHz = analysed.Average(song => song.Features.SpectralCentroidHz);
        profile.AverageLoudFractionPercent = 100f * analysed.Average(song => song.Features.LoudFraction);
        profile.MinorSharePercent = 100f * analysed.Count(song => song.Features.IsMinorKey) / analysed.Count;

        var mostCommonKey = analysed
            .Where(song => song.Features.KeyName.Length > 0)
            .GroupBy(song => song.Features.KeyName, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        profile.MostCommonKey = mostCommonKey?.Key ?? "";

        // The same numbers restricted to what was actually played, so the report can compare taste with
        // the library ("you own the slow stuff but play the fast stuff").
        var played = analysed.Where(song => song.PeriodPlays > 0).ToList();
        if (played.Count > 0)
        {
            profile.PlayedAverageBpm = played.Average(song => song.Features.Bpm);
            profile.PlayedAverageLoudness = played.Average(song => song.Features.LoudnessMean);
            profile.PlayedAverageBrightnessHz = played.Average(song => song.Features.SpectralCentroidHz);
        }

        // Tempo histogram in 10 BPM buckets, with the plays next to the song count.
        for (int from = 60; from < 200; from += 10)
        {
            int to = from + 10;
            var bucketSongs = analysed.Where(song => song.Features.Bpm >= from && song.Features.Bpm < to).ToList();
            profile.BpmHistogram.Add(new WrappedBpmBucket
            {
                FromBpm = from,
                ToBpm = to,
                SongCount = bucketSongs.Count,
                PlayCount = bucketSongs.Sum(song => song.PeriodPlays),
            });
        }

        return profile;
    }

    static List<WrappedKeyCount> BuildKeyCounts(IReadOnlyList<WrappedAnalysedSong> analysed) =>
        analysed
            .Where(song => song.Features.KeyName.Length > 0)
            .GroupBy(song => song.Features.KeyName, StringComparer.Ordinal)
            .Select(group => new WrappedKeyCount
            {
                Key = group.Key,
                SongCount = group.Count(),
                Plays = group.Sum(song => song.PeriodPlays),
                NetLikes = group.Sum(song => song.TotalLikes - song.TotalDislikes),
            })
            .OrderByDescending(entry => entry.SongCount)
            .ToList();

    // ---------------------------------------------------------------------------------------------
    //  Feature vectors and clustering
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds one standardized feature vector per song. Standardization matters: without it the clustering
    /// would be dominated by whichever feature happens to have the largest numbers (brightness in Hz,
    /// tempo in BPM), and the "sound worlds" would mostly be a tempo split.
    /// </summary>
    static double[][] BuildVectors(IReadOnlyList<WrappedAnalysedSong> songs, out double[] means, out double[] deviations)
    {
        var raw = new double[songs.Count][];
        for (int i = 0; i < songs.Count; i++)
            raw[i] = RawVector(songs[i].Features);

        means = new double[FeatureCount];
        deviations = new double[FeatureCount];
        for (int feature = 0; feature < FeatureCount; feature++)
        {
            double sum = 0.0;
            for (int i = 0; i < raw.Length; i++)
                sum += raw[i][feature];
            double mean = sum / raw.Length;

            double variance = 0.0;
            for (int i = 0; i < raw.Length; i++)
            {
                double difference = raw[i][feature] - mean;
                variance += difference * difference;
            }

            means[feature] = mean;
            // A feature that does not vary must not become a division by zero; it simply contributes nothing.
            deviations[feature] = Math.Max(1e-6, Math.Sqrt(variance / raw.Length));
        }

        for (int i = 0; i < raw.Length; i++)
            for (int feature = 0; feature < FeatureCount; feature++)
                raw[i][feature] = (raw[i][feature] - means[feature]) / deviations[feature];

        return raw;
    }

    static double[] RawVector(AudioFeatures features)
    {
        var vector = new double[FeatureCount];
        vector[0] = features.Bpm;
        vector[1] = features.LoudnessMean;
        vector[2] = features.DynamicRange;
        vector[3] = features.CrestFactorDb;
        vector[4] = features.SpectralCentroidHz;
        vector[5] = features.SpectralRolloffHz;
        vector[6] = features.SpectralFlatness;
        vector[7] = features.SpectralFlux;
        vector[8] = features.BassRatio;
        vector[9] = features.TrebleRatio;
        vector[10] = features.OnsetDensity;
        vector[11] = features.Percussiveness;
        vector[12] = -features.SpectralFlux / Math.Max(1.0, features.OnsetDensity); // steadier = higher

        // The cepstrum is compressed into three bands so one feature (the overall tilt) does not outweigh
        // the genuinely different shapes at the two ends of the spectrum.
        vector[13] = features.MfccMean.Skip(1).Take(4).DefaultIfEmpty().Average();
        vector[14] = features.MfccMean.Skip(5).Take(4).DefaultIfEmpty().Average();
        vector[15] = features.MfccMean.Skip(9).Take(4).DefaultIfEmpty().Average();
        vector[16] = features.MfccDeltaMean.Skip(1).DefaultIfEmpty().Average();
        vector[17] = features.MfccStd.Skip(1).DefaultIfEmpty().Average();

        for (int i = 0; i < FeatureCount; i++)
            if (!double.IsFinite(vector[i]))
                vector[i] = 0.0;
        return vector;
    }

    /// <summary>
    /// Prints the whole k sweep (separation score, cluster sizes and the features that dominate the split)
    /// for a set of measured songs. This is the diagnostic that distinguishes "the library really is two
    /// groups" from "the number of groups is being chosen wrongly": if the separation score keeps rising
    /// with k but a coarse k wins, the selection rule is at fault; if it peaks at k=2, the music is.
    /// </summary>
    public static void DumpClusterAnalysis(IReadOnlyList<AudioFeatures> featureSets, System.IO.TextWriter output)
    {
        var songs = featureSets
            .Select((features, index) => new WrappedAnalysedSong { SongId = Guid.Empty, Features = features, Title = $"#{index}" })
            .ToList();

        var vectors = BuildVectors(songs, out double[] means, out double[] deviations);
        output.WriteLine($"songs: {vectors.Length}, features: {FeatureCount}");

        // How much of the total variance each standardized feature contributes: if one feature dominates,
        // every split will be along that single axis and the labels will all be its two ends.
        output.WriteLine("\nper-feature variance share (a dominant feature means a one-axis split):");
        var shares = Enumerable.Range(0, FeatureCount)
            .Select(feature =>
            {
                double total = 0.0;
                for (int i = 0; i < vectors.Length; i++)
                    total += vectors[i][feature] * vectors[i][feature];
                return (Feature: feature, Share: total / vectors.Length);
            })
            .OrderByDescending(entry => entry.Share)
            .ToList();
        double shareSum = shares.Sum(entry => entry.Share);
        foreach (var entry in shares.Take(6))
            output.WriteLine($"  {FeatureNames[entry.Feature],-28} {100.0 * entry.Share / shareSum:0.0}%");

        // The clustering runs on the principal components, so the sweep has to as well - otherwise the
        // diagnostic measures a space the feature never uses.
        var projected = ProjectToPrincipalComponents(vectors, out double[] explained);
        output.WriteLine($"\nprincipal components kept: {projected[0].Length} (covering {100.0 * explained.Sum():0}% of the variance)");
        output.WriteLine("  " + string.Join("  ", explained.Select((share, index) => $"PC{index + 1} {100 * share:0.0}%")));

        output.WriteLine("\nk sweep in the component space (separation rises = a finer split is genuinely better):");
        int maxK = Math.Clamp(projected.Length / 6, 3, 10);
        double best = double.NegativeInfinity;
        int bestK = 0;
        for (int k = 2; k <= maxK; k++)
        {
            var result = RunKMeans(projected, k, restarts: 10, seed: 20240501 + k);
            result.Separation = SeparationScore(projected, result.Assignment, result.Centroids, k);

            bool accepted = bestK == 0 || result.Separation > best * MinimumSeparationGain;
            if (accepted)
            {
                best = result.Separation;
                bestK = k;
            }

            var sizes = new int[k];
            foreach (int assignment in result.Assignment)
                sizes[assignment]++;
            output.WriteLine($"  k={k,2}  separation {result.Separation,10:0.0}  sizes [{string.Join(", ", sizes.OrderByDescending(size => size))}]" +
                             (accepted ? "   <- accepted" : ""));
        }
        output.WriteLine($"chosen: k={bestK}");

        // What the chosen split separates, measured back in the original features: the component axes are
        // not interpretable, so the gap is reported for the features themselves.
        var chosen = RunKMeans(projected, bestK, restarts: 10, seed: 20240501 + bestK);
        var sizesByCluster = new int[bestK];
        foreach (int assignment in chosen.Assignment)
            sizesByCluster[assignment]++;

        output.WriteLine("\nwhat the chosen split separates (standard deviations between the extreme clusters):");
        var gaps = Enumerable.Range(0, FeatureCount)
            .Select(feature =>
            {
                double lowest = double.MaxValue;
                double highest = double.MinValue;
                for (int cluster = 0; cluster < bestK; cluster++)
                {
                    if (sizesByCluster[cluster] == 0)
                        continue;
                    double sum = 0.0;
                    int count = 0;
                    for (int i = 0; i < vectors.Length; i++)
                        if (chosen.Assignment[i] == cluster)
                        {
                            sum += vectors[i][feature];
                            count++;
                        }
                    double mean = sum / Math.Max(1, count);
                    lowest = Math.Min(lowest, mean);
                    highest = Math.Max(highest, mean);
                }
                return (Feature: feature, Gap: lowest == double.MaxValue ? 0.0 : highest - lowest);
            })
            .OrderByDescending(entry => Math.Abs(entry.Gap))
            .Take(6);
        foreach (var entry in gaps)
            output.WriteLine($"  {FeatureNames[entry.Feature],-28} {entry.Gap,7:+0.00;-0.00} sd");
    }

    /// <summary>
    /// Projects the standardized vectors onto their principal components.
    /// <para>
    /// This is what stops the grouping from collapsing into a single axis. Several of the measured features
    /// are near-duplicates of "how bright and noisy is this mix" (centroid, rolloff, treble share, the high
    /// cepstrum, flatness); clustering the raw standardized features therefore lets that one perceptual
    /// direction, multiplied by five, decide the whole split - on the reference library that produced
    /// exactly two groups whose difference was brightness and nothing else, so a bright drum &amp; bass track
    /// and a bright acoustic ballad landed together. Principal components are uncorrelated by construction,
    /// so every independent direction of variation gets one vote.
    /// </para>
    /// <para>
    /// As many components are kept as cover <see cref="MinimumVarianceExplained"/> of the variance, so noise
    /// directions are dropped and genuinely different music still can separate.
    /// </para>
    /// </summary>
    static double[][] ProjectToPrincipalComponents(double[][] vectors, out double[] explained)
    {
        int dimension = FeatureCount;
        int n = vectors.Length;

        // Covariance matrix of the standardized features.
        var covariance = new double[dimension, dimension];
        for (int i = 0; i < dimension; i++)
        {
            for (int j = i; j < dimension; j++)
            {
                double sum = 0.0;
                for (int row = 0; row < n; row++)
                    sum += vectors[row][i] * vectors[row][j];
                double value = sum / Math.Max(1, n - 1);
                covariance[i, j] = value;
                covariance[j, i] = value;
            }
        }

        JacobiEigenDecomposition(covariance, out double[] eigenvalues, out double[][] eigenvectors);

        // Eigenvalues descending; the total is the variance the components have to cover.
        var order = Enumerable.Range(0, dimension).OrderByDescending(index => eigenvalues[index]).ToArray();
        double total = Math.Max(1e-12, eigenvalues.Sum());

        double cumulative = 0.0;
        int keep = 0;
        while (keep < order.Length && cumulative / total < MinimumVarianceExplained)
        {
            cumulative += eigenvalues[order[keep]];
            keep++;
        }
        keep = Math.Clamp(keep, 2, dimension);

        explained = new double[keep];
        for (int component = 0; component < keep; component++)
            explained[component] = eigenvalues[order[component]] / total;

        var projected = new double[n][];
        for (int row = 0; row < n; row++)
        {
            var values = new double[keep];
            for (int component = 0; component < keep; component++)
            {
                double sum = 0.0;
                for (int feature = 0; feature < dimension; feature++)
                    sum += vectors[row][feature] * eigenvectors[order[component]][feature];
                values[component] = sum;
            }
            projected[row] = values;
        }

        return projected;
    }

    /// <summary>How much of the total variance the retained principal components must cover.</summary>
    const double MinimumVarianceExplained = 0.85;

    /// <summary>
    /// Eigen decomposition of a small symmetric matrix by cyclic Jacobi rotations. Deterministic (fixed
    /// sweep count, no randomness), which the clustering relies on. <paramref name="eigenvectors"/> is
    /// returned as a list of vectors, not as a matrix, because that is the only way it is used.
    /// </summary>
    static void JacobiEigenDecomposition(double[,] matrix, out double[] eigenvalues, out double[][] eigenvectors)
    {
        int size = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[size, size];
        for (int i = 0; i < size; i++)
            v[i, i] = 1.0;

        for (int sweep = 0; sweep < 100; sweep++)
        {
            double offDiagonal = 0.0;
            for (int p = 0; p < size; p++)
                for (int q = p + 1; q < size; q++)
                    offDiagonal += a[p, q] * a[p, q];
            if (offDiagonal < 1e-20)
                break;

            for (int p = 0; p < size; p++)
            {
                for (int q = p + 1; q < size; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-15)
                        continue;

                    double theta = 0.5 * (a[q, q] - a[p, p]) / a[p, q];
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    if (Math.Abs(theta) < 1e-15)
                        t = 1.0;
                    double c = 1.0 / Math.Sqrt(t * t + 1.0);
                    double s = t * c;

                    for (int k = 0; k < size; k++)
                    {
                        double akp = a[k, p];
                        double akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < size; k++)
                    {
                        double apk = a[p, k];
                        double aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < size; k++)
                    {
                        double vkp = v[k, p];
                        double vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
        }

        eigenvalues = new double[size];
        eigenvectors = new double[size][];
        for (int i = 0; i < size; i++)
        {
            eigenvalues[i] = a[i, i];
            var vector = new double[size];
            for (int k = 0; k < size; k++)
                vector[k] = v[k, i];
            eigenvectors[i] = vector;
        }
    }

    /// <summary>Result of the clustering: the chosen k, the assignment per song and the centroids.</summary>
    sealed class ClusteringResult
    {
        public int ClusterCount { get; set; }
        public int[] Assignment { get; set; } = [];
        public double[][] Centroids { get; set; } = [];
        /// <summary>Calinski-Harabasz score of the assignment: between-cluster vs. within-cluster spread.</summary>
        public double Separation { get; set; }
        /// <summary>The score the next, finer split reached (0 when there was none).</summary>
        public double NextSeparation { get; set; }
    }

    /// <summary>
    /// k-means over the standardized vectors, with k chosen by the Calinski-Harabasz score.
    /// <para>
    /// Deterministic on purpose: a wrapped is compared with the previous one, so the same library must
    /// produce the same clusters. The random restarts use a fixed seed and every comparison is a total
    /// order (score, then index), so no tie can be broken differently between runs.
    /// </para>
    /// <para>
    /// The score is the ratio of between-cluster to within-cluster spread (scaled by the degrees of
    /// freedom), which is cheap to compute from the centroids alone. A plain "more clusters is better"
    /// rule would always answer with the maximum, so a candidate has to beat the best so far by
    /// <see cref="MinimumSeparationGain"/> to win - and the winning ratio is reported
    /// (<see cref="WrappedSoundCluster.Separation"/>) instead of being hidden, so a library whose clusters
    /// are genuinely just two big groups says so.
    /// </para>
    /// </summary>
    static ClusteringResult Cluster(double[][] vectors)
    {
        // Up to ten groups for a large library, but never more groups than there is data to support.
        int maxK = Math.Clamp(vectors.Length / 6, 3, 10);
        var best = new ClusteringResult();

        for (int k = 2; k <= maxK; k++)
        {
            var candidate = RunKMeans(vectors, k, restarts: 10, seed: 20240501 + k);
            candidate.Separation = SeparationScore(vectors, candidate.Assignment, candidate.Centroids, k);

            if (best.ClusterCount == 0 || candidate.Separation > best.Separation * MinimumSeparationGain)
            {
                best = candidate;
            }
            else if (best.NextSeparation == 0)
            {
                // The first finer split that was rejected. Showing it is what makes "only N groups"
                // explainable instead of looking like an arbitrary cap.
                best.NextSeparation = candidate.Separation;
            }
        }

        return best;
    }

    /// <summary>
    /// How much better a candidate has to be before a finer split is accepted. 2% keeps the choice stable
    /// between runs (and between an analysis and the next one) while still following a real improvement.
    /// </summary>
    const double MinimumSeparationGain = 1.02;

    /// <summary>
    /// Calinski-Harabasz score: (between-cluster spread / (k - 1)) / (within-cluster spread / (n - k)).
    /// Higher means the groups are tighter and further apart. O(n * k), so evaluating every candidate k
    /// costs nothing worth optimising.
    /// </summary>
    static double SeparationScore(double[][] vectors, int[] assignment, double[][] centroids, int k)
    {
        int n = vectors.Length;
        if (n <= k || k < 2)
            return double.NegativeInfinity;

        int dimension = vectors[0].Length;
        var globalMean = new double[dimension];
        for (int i = 0; i < n; i++)
            for (int feature = 0; feature < dimension; feature++)
                globalMean[feature] += vectors[i][feature];
        for (int feature = 0; feature < dimension; feature++)
            globalMean[feature] /= n;

        var counts = new int[k];
        for (int i = 0; i < n; i++)
            counts[assignment[i]]++;

        double between = 0.0;
        double within = 0.0;
        for (int cluster = 0; cluster < k; cluster++)
        {
            if (counts[cluster] == 0)
                continue;

            double distance = 0.0;
            for (int feature = 0; feature < dimension; feature++)
            {
                double difference = centroids[cluster][feature] - globalMean[feature];
                distance += difference * difference;
            }
            between += counts[cluster] * distance;
        }

        for (int i = 0; i < n; i++)
            within += SquaredDistance(vectors[i], centroids[assignment[i]]);

        if (within <= 1e-12)
            return double.PositiveInfinity;

        return (between / (k - 1)) / (within / (n - k));
    }

    static ClusteringResult RunKMeans(double[][] vectors, int k, int restarts, int seed)
    {
        ClusteringResult? bestResult = null;
        double bestInertia = double.MaxValue;

        for (int restart = 0; restart < restarts; restart++)
        {
            var random = new Random(seed + restart * 7919);
            var centroids = InitializeCentroids(vectors, k, random);
            var assignment = new int[vectors.Length];

            for (int iteration = 0; iteration < 40; iteration++)
            {
                bool changed = false;
                for (int i = 0; i < vectors.Length; i++)
                {
                    int nearest = NearestCentroid(vectors[i], centroids);
                    if (assignment[i] != nearest)
                    {
                        assignment[i] = nearest;
                        changed = true;
                    }
                }

                // An empty cluster keeps its old centroid; with k <= n/12 that is a very rare edge case and
                // re-seeding it would make the result depend on iteration order.
                int dimension = vectors[0].Length;
                var sums = new double[k][];
                var counts = new int[k];
                for (int cluster = 0; cluster < k; cluster++)
                    sums[cluster] = new double[dimension];

                for (int i = 0; i < vectors.Length; i++)
                {
                    int cluster = assignment[i];
                    counts[cluster]++;
                    for (int feature = 0; feature < dimension; feature++)
                        sums[cluster][feature] += vectors[i][feature];
                }

                for (int cluster = 0; cluster < k; cluster++)
                {
                    if (counts[cluster] == 0)
                        continue;
                    for (int feature = 0; feature < dimension; feature++)
                        centroids[cluster][feature] = sums[cluster][feature] / counts[cluster];
                }

                if (!changed)
                    break;
            }

            double inertia = 0.0;
            for (int i = 0; i < vectors.Length; i++)
                inertia += SquaredDistance(vectors[i], centroids[assignment[i]]);

            if (inertia < bestInertia - 1e-9)
            {
                bestInertia = inertia;
                bestResult = new ClusteringResult
                {
                    ClusterCount = k,
                    Assignment = (int[])assignment.Clone(),
                    Centroids = centroids,
                };
            }
        }

        return bestResult ?? new ClusteringResult { ClusterCount = k, Assignment = new int[vectors.Length], Centroids = InitializeCentroids(vectors, k, new Random(seed)) };
    }

    /// <summary>k-means++ seeding: the first centroid is random, the rest are chosen with probability proportional to their distance from the closest existing centroid.</summary>
    static double[][] InitializeCentroids(double[][] vectors, int k, Random random)
    {
        var centroids = new double[k][];
        int first = random.Next(vectors.Length);
        centroids[0] = (double[])vectors[first].Clone();

        var distances = new double[vectors.Length];
        for (int i = 0; i < vectors.Length; i++)
            distances[i] = SquaredDistance(vectors[i], centroids[0]);

        for (int cluster = 1; cluster < k; cluster++)
        {
            double total = distances.Sum();
            int chosen;
            if (total <= 1e-12)
            {
                chosen = random.Next(vectors.Length);
            }
            else
            {
                double target = random.NextDouble() * total;
                double running = 0.0;
                chosen = vectors.Length - 1;
                for (int i = 0; i < vectors.Length; i++)
                {
                    running += distances[i];
                    if (running >= target)
                    {
                        chosen = i;
                        break;
                    }
                }
            }

            centroids[cluster] = (double[])vectors[chosen].Clone();
            for (int i = 0; i < vectors.Length; i++)
                distances[i] = Math.Min(distances[i], SquaredDistance(vectors[i], centroids[cluster]));
        }

        return centroids;
    }

    static int NearestCentroid(double[] vector, double[][] centroids)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int cluster = 0; cluster < centroids.Length; cluster++)
        {
            double distance = SquaredDistance(vector, centroids[cluster]);
            // Strictly less than, so ties resolve to the lower index deterministically.
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = cluster;
            }
        }
        return best;
    }

    static double SquaredDistance(double[] first, double[] second)
    {
        double sum = 0.0;
        for (int i = 0; i < first.Length; i++)
        {
            double difference = first[i] - second[i];
            sum += difference * difference;
        }
        return sum;
    }

    // ---------------------------------------------------------------------------------------------
    //  Cluster descriptions
    // ---------------------------------------------------------------------------------------------

    static List<WrappedSoundCluster> BuildClusters(
        IReadOnlyList<WrappedAnalysedSong> songs,
        double[][] vectors,
        ClusteringResult clustering,
        double[] means,
        double[] deviations,
        int periodPlayTotal)
    {
        var clusters = new List<WrappedSoundCluster>();
        if (clustering.ClusterCount == 0)
            return clusters;

        for (int index = 0; index < clustering.ClusterCount; index++)
        {
            var members = Enumerable.Range(0, songs.Count).Where(i => clustering.Assignment[i] == index).ToList();
            if (members.Count == 0)
                continue;

            var cluster = new WrappedSoundCluster
            {
                ClusterIndex = index,
                SongCount = members.Count,
                LibrarySharePercent = 100f * members.Count / songs.Count,
                Plays = members.Sum(i => songs[i].PeriodPlays),
                AverageBpm = (float)members.Average(i => songs[i].Features.Bpm),
                AverageLoudness = (float)members.Average(i => songs[i].Features.LoudnessMean),
                AverageBrightnessHz = (float)members.Average(i => songs[i].Features.SpectralCentroidHz),
                NetLikes = members.Sum(i => songs[i].TotalLikes - songs[i].TotalDislikes),
                Separation = (float)clustering.Separation,
                NextSplitSeparation = (float)clustering.NextSeparation,
                ClusterCount = clustering.ClusterCount,
            };

            cluster.PlaySharePercent = periodPlayTotal > 0 ? 100f * cluster.Plays / periodPlayTotal : 0f;
            cluster.PlaysPerSong = cluster.Plays / (double)members.Count;
            cluster.UntouchedPercent = 100f * members.Count(i => songs[i].PeriodPlays == 0) / members.Count;

            // What makes this cluster different. The clustering ran on principal components, whose axes are
            // not interpretable (and whose length is not the feature count), so the description is derived
            // here from the ORIGINAL standardized features: this cluster's mean feature value, which is the
            // deviation from the library average in standard deviations.
            var deviationsByFeature = Enumerable.Range(0, FeatureCount)
                .Select(feature => (Feature: feature, Z: members.Average(i => vectors[i][feature])))
                .OrderByDescending(entry => Math.Abs(entry.Z))
                .ToList();

            cluster.Characteristics = deviationsByFeature
                .Take(4)
                .Where(entry => Math.Abs(entry.Z) >= 0.5)
                .Select(entry => $"{Describe(entry.Feature, entry.Z)} ({entry.Z:+0.0;-0.0;0.0} sd)")
                .ToList();

            cluster.Label = SoundClusterLabeler.BuildLabel(
                deviationsByFeature.Where(entry => Math.Abs(entry.Z) >= 0.5).Select(entry => (entry.Feature, entry.Z)).ToList(),
                cluster,
                !string.IsNullOrEmpty(songs[members[0]].Title) ? songs[members[0]].Title : "");

            cluster.TopSongs = members
                .OrderByDescending(i => songs[i].PeriodPlays)
                .ThenBy(i => songs[i].Title, StringComparer.Ordinal)
                .Take(8)
                .Select(i => new WrappedSong
                {
                    SongId = songs[i].SongId,
                    Name = songs[i].Title,
                    Artist = songs[i].Artist,
                    Album = songs[i].Album,
                    FilePath = songs[i].FilePath,
                    HasFile = true,
                    Plays = songs[i].PeriodPlays,
                    Upvotes = songs[i].PeriodUpvotes,
                    Downvotes = songs[i].PeriodDownvotes,
                    TotalLikes = songs[i].TotalLikes,
                    TotalDislikes = songs[i].TotalDislikes,
                    Score = songs[i].Score,
                    DateAdded = songs[i].DateAdded,
                })
                .ToList();

            cluster.TopArtists = members
                .Where(i => songs[i].Artist.Length > 0)
                .GroupBy(i => ArtistNameParser.ArtistKey(songs[i].Artist), StringComparer.Ordinal)
                .OrderByDescending(group => group.Sum(i => songs[i].PeriodPlays))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(4)
                .Select(group => ArtistNameParser.ChooseDisplayName(group.Select(i => songs[i].Artist)))
                .ToList();

            clusters.Add(cluster);
        }

        _ = means;
        _ = deviations;
        return clusters.OrderByDescending(cluster => cluster.SongCount).ToList();
    }
    /// <summary>One feature, said in words, using the sign of its deviation to pick the direction.</summary>
    static string Describe(int feature, double z) => feature switch
    {
        0 => z > 0 ? "fast" : "slow",
        1 => z > 0 ? "loud" : "quiet",
        2 => z > 0 ? "wide dynamic range" : "flat dynamics",
        3 => z > 0 ? "punchy transients" : "smooth transients",
        4 => z > 0 ? "bright" : "dark",
        5 => z > 0 ? "airy top end" : "rolled-off top end",
        6 => z > 0 ? "noisy, dense texture" : "clean, tonal texture",
        7 => z > 0 ? "restless" : "steady",
        8 => z > 0 ? "bass heavy" : "bass light",
        9 => z > 0 ? "treble heavy" : "little treble",
        10 => z > 0 ? "busy rhythm" : "sparse rhythm",
        11 => z > 0 ? "percussive" : "unpercussive",
        12 => z > 0 ? "harmonically stable" : "harmonically shifting",
        13 => z > 0 ? "warm timbre" : "thin timbre",
        14 => z > 0 ? "forward mids" : "scooped mids",
        15 => z > 0 ? "gritty detail" : "smooth detail",
        16 => z > 0 ? "shifting arrangement" : "static arrangement",
        17 => z > 0 ? "varied sections" : "uniform sections",
        _ => "distinct",
    };

    // ---------------------------------------------------------------------------------------------
    //  Signature song and cross insights
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The song in the period that best represents the listener's own average sound: the smallest distance
    /// to the mean vector of everything they played. It answers "if one song had to stand for this year,
    /// which one sounds most like it".
    /// </summary>
    static void BuildSignatureSongs(WrappedReport report, IReadOnlyList<WrappedAnalysedSong> analysed)
    {
        var played = analysed.Where(song => song.PeriodPlays > 0).ToList();
        if (played.Count < 5)
            return;

        var vectors = BuildVectors(played, out _, out _);
        var mean = new double[FeatureCount];
        for (int feature = 0; feature < FeatureCount; feature++)
            mean[feature] = vectors.Average(vector => vector[feature]);

        int bestIndex = 0;
        int worstIndex = 0;
        double bestDistance = double.MaxValue;
        double worstDistance = double.MinValue;
        for (int i = 0; i < vectors.Length; i++)
        {
            double distance = SquaredDistance(vectors[i], mean);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
            if (distance > worstDistance)
            {
                worstDistance = distance;
                worstIndex = i;
            }
        }

        report.SignatureSong = ToWrappedSong(played[bestIndex]);
        report.SignatureSong.Reason = $"Closest to the average sound of the {played.Count} songs played in this period";

        report.OutlierSong = ToWrappedSong(played[worstIndex]);
        report.OutlierSong.Reason = $"The song that sounds least like everything else you played in this period";
    }

    static WrappedSong ToWrappedSong(WrappedAnalysedSong song) => new()
    {
        SongId = song.SongId,
        Name = song.Title,
        Artist = song.Artist,
        Album = song.Album,
        FilePath = song.FilePath,
        HasFile = true,
        Plays = song.PeriodPlays,
        Upvotes = song.PeriodUpvotes,
        Downvotes = song.PeriodDownvotes,
        TotalLikes = song.TotalLikes,
        TotalDislikes = song.TotalDislikes,
        Score = song.Score,
        DateAdded = song.DateAdded,
    };

    static List<string> BuildClusterHighlights(WrappedReport report, int periodPlayTotal)
    {
        var highlights = new List<string>();
        var profile = report.AudioProfile;

        if (report.SoundClusters.Count > 0 && periodPlayTotal > 0)
        {
            var mostPlayed = report.SoundClusters.OrderByDescending(cluster => cluster.PlaysPerSong).First();
            var biggest = report.SoundClusters.OrderByDescending(cluster => cluster.SongCount).First();
            var mostIgnored = report.SoundClusters.OrderBy(cluster => cluster.PlaysPerSong).First();

            highlights.Add($"The sound world you reach for most is \"{mostPlayed.Label}\": {mostPlayed.PlaysPerSong:0.0} plays per song in it.");
            if (mostIgnored.ClusterIndex != mostPlayed.ClusterIndex)
                highlights.Add($"The one you keep skipping over is \"{mostIgnored.Label}\" ({mostIgnored.PlaysPerSong:0.0} plays per song, {mostIgnored.UntouchedPercent:0}% of it untouched).");
            highlights.Add($"Your library splits into {report.SoundClusters.Count} sound worlds; the biggest, \"{biggest.Label}\", holds {biggest.LibrarySharePercent:0}% of it.");

            if (profile.PlayedAverageBpm > 0 && Math.Abs(profile.PlayedAverageBpm - profile.AverageBpm) > 4)
            {
                string direction = profile.PlayedAverageBpm > profile.AverageBpm ? "faster" : "slower";
                highlights.Add($"What you play is {direction} than what you own: {profile.PlayedAverageBpm:0} vs {profile.AverageBpm:0} BPM on average.");
            }

            if (profile.PlayedAverageBrightnessHz > 0 && profile.AverageBrightnessHz > 0
                && Math.Abs(profile.PlayedAverageBrightnessHz - profile.AverageBrightnessHz) / profile.AverageBrightnessHz > 0.12)
            {
                string direction = profile.PlayedAverageBrightnessHz > profile.AverageBrightnessHz ? "brighter" : "darker";
                highlights.Add($"The music you actually play is {direction} than the library average.");
            }

            if (profile.MinorSharePercent >= 60)
                highlights.Add($"{profile.MinorSharePercent:0}% of the analysed library is in a minor key.");
        }

        return highlights;
    }
}
