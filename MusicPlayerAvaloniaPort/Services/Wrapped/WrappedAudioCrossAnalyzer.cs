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
        var clusters = Cluster(vectors, means, deviations);

        report.SoundClusters = BuildClusters(analysed, vectors, clusters, means, deviations, periodPlayTotal);
        BuildSignatureSongs(report, analysed);
        report.Highlights.AddRange(BuildClusterHighlights(report, periodPlayTotal));
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

    /// <summary>Result of the clustering: the chosen k, the assignment per song and the centroids.</summary>
    sealed class ClusteringResult
    {
        public int ClusterCount { get; set; }
        public int[] Assignment { get; set; } = [];
        public double[][] Centroids { get; set; } = [];
        public double Silhouette { get; set; }
    }

    /// <summary>
    /// k-means over the standardized vectors, with k chosen by silhouette score.
    /// <para>
    /// Deterministic on purpose: a wrapped is compared with the previous one, so the same library must
    /// produce the same clusters. The random restarts use a fixed seed and every comparison is a total
    /// order (score, then index), so no tie can be broken differently between runs.
    /// </para>
    /// </summary>
    static ClusteringResult Cluster(double[][] vectors, double[] means, double[] deviations)
    {
        _ = means;
        _ = deviations;

        int maxK = Math.Clamp(vectors.Length / 12, 3, 8);
        var best = new ClusteringResult();

        for (int k = 2; k <= maxK; k++)
        {
            var candidate = RunKMeans(vectors, k, restarts: 8, seed: 20240501 + k);
            double silhouette = SilhouetteScore(vectors, candidate.Assignment, k);
            candidate.Silhouette = silhouette;

            // Prefer more clusters only when they are clearly better separated (a 5% margin avoids
            // flipping between 5 and 6 clusters because of numerical noise).
            if (silhouette > best.Silhouette * 1.05 || best.ClusterCount == 0)
            {
                best = candidate;
            }
        }

        return best;
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
                var sums = new double[k][];
                var counts = new int[k];
                for (int cluster = 0; cluster < k; cluster++)
                    sums[cluster] = new double[FeatureCount];

                for (int i = 0; i < vectors.Length; i++)
                {
                    int cluster = assignment[i];
                    counts[cluster]++;
                    for (int feature = 0; feature < FeatureCount; feature++)
                        sums[cluster][feature] += vectors[i][feature];
                }

                for (int cluster = 0; cluster < k; cluster++)
                {
                    if (counts[cluster] == 0)
                        continue;
                    for (int feature = 0; feature < FeatureCount; feature++)
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

    /// <summary>
    /// Mean silhouette of the assignment: how much closer each song is to its own cluster than to the
    /// nearest other one. This is what makes "how many sound worlds are there" a measured decision rather
    /// than a number somebody picked.
    /// </summary>
    static double SilhouetteScore(double[][] vectors, int[] assignment, int k)
    {
        // Sampled for large libraries: the score is used to compare values of k, not as an exact figure.
        int sampleSize = Math.Min(vectors.Length, 1200);
        var indexes = Enumerable.Range(0, vectors.Length).ToArray();
        if (vectors.Length > sampleSize)
        {
            var random = new Random(4242);
            for (int i = indexes.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (indexes[i], indexes[j]) = (indexes[j], indexes[i]);
            }
            indexes = indexes.Take(sampleSize).ToArray();
        }

        var totals = new double[k][];
        var counts = new int[k];
        for (int cluster = 0; cluster < k; cluster++)
            totals[cluster] = new double[FeatureCount];
        for (int i = 0; i < vectors.Length; i++)
        {
            int cluster = assignment[i];
            counts[cluster]++;
            for (int feature = 0; feature < FeatureCount; feature++)
                totals[cluster][feature] += vectors[i][feature];
        }

        var centroids = new double[k][];
        for (int cluster = 0; cluster < k; cluster++)
        {
            centroids[cluster] = new double[FeatureCount];
            if (counts[cluster] == 0)
                continue;
            for (int feature = 0; feature < FeatureCount; feature++)
                centroids[cluster][feature] = totals[cluster][feature] / counts[cluster];
        }

        double sum = 0.0;
        int counted = 0;
        foreach (int i in indexes)
        {
            int own = assignment[i];
            if (counts[own] <= 1)
                continue;

            // a(i): mean distance to the other members of the own cluster; b(i): mean distance to the
            // members of the closest other cluster.
            double ownSum = 0.0;
            int ownCount = 0;
            var otherSums = new double[k];
            var otherCounts = new int[k];
            for (int j = 0; j < vectors.Length; j++)
            {
                if (i == j)
                    continue;
                double distance = Math.Sqrt(SquaredDistance(vectors[i], vectors[j]));
                int cluster = assignment[j];
                if (cluster == own)
                {
                    ownSum += distance;
                    ownCount++;
                }
                else
                {
                    otherSums[cluster] += distance;
                    otherCounts[cluster]++;
                }
            }

            if (ownCount == 0)
                continue;

            double a = ownSum / ownCount;
            double b = double.MaxValue;
            for (int cluster = 0; cluster < k; cluster++)
                if (cluster != own && otherCounts[cluster] > 0)
                    b = Math.Min(b, otherSums[cluster] / otherCounts[cluster]);

            if (b == double.MaxValue)
                continue;

            double denominator = Math.Max(a, b);
            if (denominator > 1e-12)
                sum += (b - a) / denominator;
            counted++;
        }

        return counted > 0 ? sum / counted : double.NegativeInfinity;
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
            };

            cluster.PlaySharePercent = periodPlayTotal > 0 ? 100f * cluster.Plays / periodPlayTotal : 0f;
            cluster.PlaysPerSong = cluster.Plays / (double)members.Count;
            cluster.UntouchedPercent = 100f * members.Count(i => songs[i].PeriodPlays == 0) / members.Count;

            // What makes this cluster different: the features whose centroid deviates most from the
            // library average, in standard deviations.
            var deviationsByFeature = Enumerable.Range(0, FeatureCount)
                .Select(feature => (Feature: feature, Z: clustering.Centroids[index][feature]))
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
