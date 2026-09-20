using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicPlayerAvaloniaPort.Persistence;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// Persistence for the wrapped reports.
/// <para>
/// Each report is one JSON file next to an index that describes it, so listing what exists (the year
/// picker in the UI) never has to read the reports themselves - a report for a big library is a
/// multi-megabyte document and the view only needs its title and date to draw the list.
/// </para>
/// </summary>
public sealed class WrappedStore
{
    /// <summary>One entry of the index: just enough for the UI to list the report.</summary>
    public sealed class WrappedIndexEntry
    {
        /// <summary>File name inside the wrapped folder (e.g. "2025.json" or "all-time.json").</summary>
        public string FileName { get; set; } = "";
        public int? Year { get; set; }
        public string PeriodLabel { get; set; } = "";
        public DateTimeOffset ComputedAt { get; set; }
        public int Plays { get; set; }
        public int DistinctSongs { get; set; }
        public int DistinctArtists { get; set; }
        public int AnalysedSongs { get; set; }
        public double ComputationSeconds { get; set; }
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    sealed class WrappedIndex
    {
        public int Version { get; set; } = WrappedReport.SchemaVersion;
        public List<WrappedIndexEntry> Entries { get; set; } = [];
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>All reports that exist on disk, newest first.</summary>
    public List<WrappedIndexEntry> LoadIndex()
    {
        try
        {
            string path = PersistenceLocations.WrappedIndexPath;
            if (!File.Exists(path))
                return [];

            var index = JsonSerializer.Deserialize<WrappedIndex>(File.ReadAllText(path), JsonOptions);
            if (index == null)
                return [];

            return index.Entries
                .OrderByDescending(entry => entry.Year ?? int.MaxValue)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the wrapped index: {ex.Message}");
            return [];
        }
    }

    /// <summary>Reads one report, or null when the file is missing or was written by an incompatible version.</summary>
    public WrappedReport? Load(WrappedIndexEntry entry)
    {
        try
        {
            string path = Path.Combine(PersistenceLocations.WrappedDirectory, entry.FileName);
            if (!File.Exists(path))
                return null;

            var report = JsonSerializer.Deserialize<WrappedReport>(File.ReadAllText(path), JsonOptions);
            if (report == null || report.Version != WrappedReport.SchemaVersion)
                return null;

            return report;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the wrapped report {entry.FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes a report and updates the index.</summary>
    public void Save(WrappedReport report)
    {
        Directory.CreateDirectory(PersistenceLocations.WrappedDirectory);
        string fileName = FileNameFor(report.Year);
        string path = Path.Combine(PersistenceLocations.WrappedDirectory, fileName);

        // Written to a temporary file first: a crash or a cancel halfway through must never leave a
        // truncated report that the index claims is complete.
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporaryPath, path, true);

        var entries = LoadIndex();
        entries.RemoveAll(entry => entry.FileName == fileName);
        entries.Add(new WrappedIndexEntry
        {
            FileName = fileName,
            Year = report.Year,
            PeriodLabel = report.PeriodLabel,
            ComputedAt = report.ComputedAt,
            Plays = report.Headline.Plays,
            DistinctSongs = report.Headline.DistinctSongs,
            DistinctArtists = report.Headline.DistinctArtists,
            AnalysedSongs = report.SongsAnalysed,
            ComputationSeconds = report.ComputationSeconds,
            UserId = report.UserId,
            DisplayName = report.DisplayName,
        });

        var index = new WrappedIndex { Entries = entries.OrderByDescending(entry => entry.Year ?? int.MaxValue).ToList() };
        File.WriteAllText(PersistenceLocations.WrappedIndexPath, JsonSerializer.Serialize(index, JsonOptions));
    }

    /// <summary>Removes a report and its index entry.</summary>
    public void Delete(WrappedIndexEntry entry)
    {
        try
        {
            string path = Path.Combine(PersistenceLocations.WrappedDirectory, entry.FileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not delete the wrapped report {entry.FileName}: {ex.Message}");
        }

        var entries = LoadIndex();
        entries.RemoveAll(candidate => candidate.FileName == entry.FileName);
        var index = new WrappedIndex { Entries = entries };
        File.WriteAllText(PersistenceLocations.WrappedIndexPath, JsonSerializer.Serialize(index, JsonOptions));
    }

    static string FileNameFor(int? year) => year is int value ? $"{value}.json" : "all-time.json";
}
