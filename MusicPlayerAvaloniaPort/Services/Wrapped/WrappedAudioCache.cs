using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicPlayerAvaloniaPort.Persistence;
using MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// One song's cached audio analysis. Plain settable properties only, because it is the JSON format of
/// <see cref="WrappedAudioCache"/> - adding a property later is safe, renaming one is not.
/// </summary>
public sealed class CachedAudioFeatures
{
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    /// <summary>File modification time in UTC ticks; the cache entry is stale when this changes.</summary>
    public long FileModifiedUtcTicks { get; set; }
    public DateTimeOffset AnalyzedAt { get; set; }
    public AudioFeatures Features { get; set; } = new();
}

/// <summary>
/// Durable cache of the per-song audio analysis.
/// <para>
/// Analysing a library means decoding every file once, which on a NAS is minutes to hours - far too much
/// to repeat whenever the user opens the wrapped. The result for a file never changes unless the file
/// changes, so it is kept in one JSON document next to the wrapped results and validated against the
/// file's size and modification time.
/// </para>
/// <para>
/// The cache is written incrementally while a run is in progress (see <see cref="Save"/> being called
/// after every batch), so cancelling or crashing a long run never throws away the work already done -
/// the next run continues where the last one stopped.
/// </para>
/// </summary>
public sealed class WrappedAudioCache
{
    /// <summary>Bump when <see cref="CachedAudioFeatures"/> or <see cref="AudioFeatures"/> changes shape.</summary>
    public const int SchemaVersion = 1;

    /// <summary>How often an in-progress run flushes the cache to disk.</summary>
    public const int FlushEveryAnalyses = 25;

    sealed class CacheDocument
    {
        public int Version { get; set; } = SchemaVersion;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
        /// <summary>Keyed by <see cref="CacheKey"/> of the file path.</summary>
        public Dictionary<string, CachedAudioFeatures> Entries { get; set; } = new();
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly Dictionary<string, CachedAudioFeatures> entries;
    readonly object sync = new();
    int pendingAnalyses;

    public WrappedAudioCache()
    {
        entries = Load();
    }

    public int Count
    {
        get
        {
            lock (sync)
                return entries.Count;
        }
    }

    /// <summary>
    /// Returns the cached analysis of a file when it is still valid, otherwise null. A file that changed
    /// (different size or modification time) is treated as uncached, so editing a song's tags or replacing
    /// the file re-analyses it instead of showing stale numbers.
    /// </summary>
    public AudioFeatures? TryGet(string filePath)
    {
        string key = CacheKey(filePath);
        CachedAudioFeatures? entry;
        lock (sync)
        {
            if (!entries.TryGetValue(key, out entry))
                return null;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length != entry.FileSize || info.LastWriteTimeUtc.Ticks != entry.FileModifiedUtcTicks)
                return null;
        }
        catch (Exception)
        {
            // Unreadable file: treat as uncached and let the analysis report it.
            return null;
        }

        return entry.Features;
    }

    /// <summary>Stores the analysis of a file (replacing an older entry for the same path).</summary>
    public void Store(string filePath, AudioFeatures features, bool flushNow = false)
    {
        long size = 0;
        long modifiedTicks = 0;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
            {
                size = info.Length;
                modifiedTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception)
        {
            // Leave the stamps at zero; the entry will simply never validate.
        }

        var entry = new CachedAudioFeatures
        {
            FilePath = filePath,
            FileSize = size,
            FileModifiedUtcTicks = modifiedTicks,
            AnalyzedAt = DateTimeOffset.Now,
            Features = features,
        };

        lock (sync)
        {
            entries[CacheKey(filePath)] = entry;
            pendingAnalyses++;
        }

        if (flushNow)
            Save();
    }

    /// <summary>Flushes to disk once <see cref="FlushEveryAnalyses"/> new analyses accumulated.</summary>
    public void SaveIfDue()
    {
        lock (sync)
        {
            if (pendingAnalyses < FlushEveryAnalyses)
                return;
            pendingAnalyses = 0;
        }
        Save();
    }

    /// <summary>Writes the cache document.</summary>
    public void Save()
    {
        try
        {
            var document = new CacheDocument { UpdatedAt = DateTimeOffset.Now };
            lock (sync)
            {
                pendingAnalyses = 0;
                foreach (var pair in entries)
                    document.Entries[pair.Key] = pair.Value;
            }

            string directory = PersistenceLocations.WrappedDirectory;
            Directory.CreateDirectory(directory);
            File.WriteAllText(PersistenceLocations.WrappedAudioCachePath, JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception ex)
        {
            // A cache that cannot be written must not fail the run - the wrapped results themselves are
            // what the user asked for.
            Console.WriteLine($"Could not write the wrapped audio cache: {ex.Message}");
        }
    }

    static Dictionary<string, CachedAudioFeatures> Load()
    {
        try
        {
            string path = PersistenceLocations.WrappedAudioCachePath;
            if (!File.Exists(path))
                return new Dictionary<string, CachedAudioFeatures>(StringComparer.Ordinal);

            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), JsonOptions);
            if (document == null || document.Version != SchemaVersion)
                return new Dictionary<string, CachedAudioFeatures>(StringComparer.Ordinal);

            return new Dictionary<string, CachedAudioFeatures>(document.Entries, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the wrapped audio cache (recomputing): {ex.Message}");
            return new Dictionary<string, CachedAudioFeatures>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// A stable key for a file path. Short, so the cache document stays small for a large library; the
    /// chance of a collision is not a correctness problem because the full path is validated on read.
    /// </summary>
    static string CacheKey(string filePath)
    {
        string normalized = filePath.Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();

        // FNV-1a over the normalized path.
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (char c in normalized)
            {
                hash ^= c;
                hash *= prime;
            }
            return hash.ToString("x16");
        }
    }
}
