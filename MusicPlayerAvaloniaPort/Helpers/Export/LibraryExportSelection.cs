using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Helpers.Export;

/// <summary>
/// The thresholds of an export, as configured in the export library view (the DxMGP client's export
/// chooser had sliders for score, trend, vote ratio and play chance; the size limit is an addition of this
/// port, since the amount of storage the target device has is the actual constraint).
/// </summary>
public readonly record struct ExportThresholds(
    float MinScore,
    int MinStreak,
    float MinVoteRatio,
    float MinPlayChancePercent,
    double MaxSizeMb);

/// <summary>
/// Picks the songs of an export from all library songs (see <see cref="ExportCandidate"/>). Pure logic on
/// purpose: the export library view only collects the data and shows the result.
/// </summary>
public static class LibraryExportSelection
{
    public const double NoSizeLimit = 0;

    /// <summary>
    /// The songs that pass all thresholds, best (highest score) first, like the DxMGP export chooser
    /// ordered its selection.
    ///
    /// With a size limit the list is trimmed to the best songs that still fit into it: the score-ordered
    /// list is walked and a song is only taken while it fits into the remaining budget, but smaller songs
    /// further down can still make it in. Songs whose size is unknown (the measurement did not look at them
    /// yet) are skipped when a limit is used - they cannot be counted against the budget - and kept
    /// otherwise.
    /// </summary>
    public static List<ExportCandidate> SelectSongs(IEnumerable<ExportCandidate> songs, ExportThresholds thresholds)
    {
        List<ExportCandidate> passing = [.. songs
            .Where(song => PassesThresholds(song, thresholds))
            .OrderByDescending(song => song.Score)];

        if (thresholds.MaxSizeMb <= NoSizeLimit)
            return passing;

        long remainingBytes = (long)(thresholds.MaxSizeMb * 1024 * 1024);
        List<ExportCandidate> fitting = [];
        foreach (ExportCandidate song in passing)
        {
            if (song.SizeBytes is not long size || size <= 0 || size > remainingBytes)
                continue;

            remainingBytes -= size;
            fitting.Add(song);
        }

        return fitting;
    }

    public static bool PassesThresholds(ExportCandidate song, ExportThresholds thresholds) =>
        song.Score >= thresholds.MinScore
        && song.Streak >= thresholds.MinStreak
        && song.VoteRatio >= thresholds.MinVoteRatio
        && song.PlayChancePercent >= thresholds.MinPlayChancePercent;

    /// <summary>Total size of the given songs in bytes; songs without a measured size count as 0.</summary>
    public static long TotalSizeBytes(IEnumerable<ExportCandidate> songs) =>
        songs.Sum(song => song.SizeBytes ?? 0);
}
