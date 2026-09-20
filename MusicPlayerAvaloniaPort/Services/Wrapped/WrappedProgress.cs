using System;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>The phases a wrapped computation goes through, in order.</summary>
public enum WrappedStage
{
    Idle,
    /// <summary>Reading the history and the song rows out of the local database.</summary>
    ReadingHistory,
    /// <summary>Resolving which songs have a file in the library folder.</summary>
    ResolvingFiles,
    /// <summary>Decoding and measuring the song files (the slow part).</summary>
    AnalyzingAudio,
    /// <summary>Reading release information for the songs that could be matched online.</summary>
    EnrichingOnline,
    /// <summary>Aggregating the listening history.</summary>
    AnalyzingHistory,
    /// <summary>Grouping the songs by sound.</summary>
    ClusteringSound,
    /// <summary>Building and writing the report(s).</summary>
    BuildingReports,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// Progress of a running wrapped computation. Reported often enough for a smooth bar, but not so often
/// that the UI is flooded - the audio stage reports once per analysed file.
/// </summary>
public sealed class WrappedProgress
{
    public WrappedStage Stage { get; init; } = WrappedStage.Idle;

    /// <summary>Overall progress over the requested years.</summary>
    public double YearFraction { get; init; }
    /// <summary>Index of the year being computed (1 based), and how many there are.</summary>
    public int YearIndex { get; init; }
    public int YearCount { get; init; }
    /// <summary>Label of the year being computed ("2025" or "All time").</summary>
    public string YearLabel { get; init; } = "";

    /// <summary>Progress inside the current stage, 0..1.</summary>
    public double StageFraction { get; init; }
    /// <summary>Readable one-liner for the UI.</summary>
    public string Message { get; init; } = "";

    /// <summary>Items finished in the current stage (files analysed, songs read, ...).</summary>
    public int ItemsDone { get; init; }
    /// <summary>Estimated total items of the current stage (0 when unknown).</summary>
    public int ItemsTotal { get; init; }

    /// <summary>Estimated time left, when enough of the run has happened to estimate it.</summary>
    public TimeSpan? EstimatedRemaining { get; init; }
    /// <summary>Analysis cache hits in the audio stage (the run gets much faster once these dominate).</summary>
    public int CacheHits { get; init; }
    /// <summary>Analyses that had to be done from scratch.</summary>
    public int Analysed { get; init; }
    /// <summary>Files that could not be analysed (missing or unreadable).</summary>
    public int Unreadable { get; init; }
    /// <summary>Running time of the whole computation.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Fraction of the whole run, for a single progress bar.</summary>
    public double OverallFraction
    {
        get
        {
            if (YearCount <= 0)
                return 0;
            double completedYears = Math.Max(0, YearIndex - 1);
            double currentYear = StageFraction;
            return Math.Clamp((completedYears + currentYear) / YearCount, 0, 1);
        }
    }

    /// <summary>A short summary of the audio stage for the status line.</summary>
    public string AudioSummary => ItemsTotal > 0
        ? $"{ItemsDone}/{ItemsTotal} files ({CacheHits} cached, {Analysed} analysed, {Unreadable} unreadable)"
        : $"{ItemsDone} files ({CacheHits} cached, {Analysed} analysed, {Unreadable} unreadable)";
}
