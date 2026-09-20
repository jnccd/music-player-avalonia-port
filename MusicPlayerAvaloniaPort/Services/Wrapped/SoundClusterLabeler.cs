using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// Names a sound cluster from the cluster's own measurements.
/// <para>
/// This is the honest alternative to a genre label. Nobody told the analysis what "drum and bass" or
/// "ambient" is, and asking an online database is exactly what does not work for this library - so a
/// cluster is described by what it measurably is ("fast, bright and busy"), with the two most distinctive
/// features driving the wording and the numbers shown next to it.
/// </para>
/// </summary>
public static class SoundClusterLabeler
{
    /// <summary>
    /// Builds a short label from the features with the largest deviation from the library average.
    /// </summary>
    /// <param name="distinctiveFeatures">Feature index + z-score pairs, most distinctive first.</param>
    /// <param name="cluster">The cluster being labelled (used for the tempo/energy fallback wording).</param>
    /// <param name="exampleTitle">A song title from the cluster, used only as a last-resort disambiguator.</param>
    public static string BuildLabel(
        IReadOnlyList<(int Feature, double Z)> distinctiveFeatures,
        WrappedSoundCluster cluster,
        string exampleTitle)
    {
        if (distinctiveFeatures.Count == 0)
        {
            // A cluster that sits on the library average everywhere: describe it by its most obvious
            // absolute property instead of pretending a feature stands out.
            return $"Middle of the road ({cluster.AverageBpm:0} BPM)";
        }

        var parts = new List<string>();
        foreach (var (feature, z) in distinctiveFeatures.Take(3))
        {
            string word = Word(feature, z);
            if (word.Length > 0 && !parts.Contains(word, StringComparer.OrdinalIgnoreCase))
                parts.Add(word);
        }

        if (parts.Count == 0)
            return $"Unclassified ({cluster.AverageBpm:0} BPM)";

        string label = JoinWords(parts);
        return char.ToUpperInvariant(label[0]) + label[1..];
    }

    /// <summary>One adjective for a feature and the direction it deviates in.</summary>
    static string Word(int feature, double z) => feature switch
    {
        0 => z > 0 ? "fast" : "slow",
        1 => z > 0 ? "loud" : "quiet",
        2 => z > 0 ? "dynamic" : "compressed",
        3 => z > 0 ? "punchy" : "smooth",
        4 => z > 0 ? "bright" : "dark",
        5 => z > 0 ? "airy" : "muffled",
        6 => z > 0 ? "gritty" : "clean",
        7 => z > 0 ? "restless" : "steady",
        8 => z > 0 ? "bass-heavy" : "bass-light",
        9 => z > 0 ? "treble-heavy" : "dull",
        10 => z > 0 ? "busy" : "sparse",
        11 => z > 0 ? "percussive" : "unpercussive",
        12 => z > 0 ? "stable" : "restless harmony",
        13 => z > 0 ? "warm" : "thin",
        14 => z > 0 ? "forward" : "scooped",
        15 => z > 0 ? "detailed" : "blunt",
        16 => z > 0 ? "changing" : "static",
        17 => z > 0 ? "varied" : "uniform",
        _ => "",
    };

    /// <summary>Turns the adjective list into readable English ("fast, bright and busy").</summary>
    static string JoinWords(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
