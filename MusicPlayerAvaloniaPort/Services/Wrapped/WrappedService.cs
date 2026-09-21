using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayerAvaloniaPort;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;
/// <summary>What a wrapped run should include. Everything expensive is opt-in.</summary>
public sealed class WrappedOptions
{
    /// <summary>Calendar years to produce (plus the all-time report). Empty = every year that has history.</summary>
    public List<int> Years { get; set; } = [];

    /// <summary>
    /// Analyse the song files. This is the slow part (decoding every file once) and the only one that
    /// needs the library to be reachable - with the cache warm a re-run is quick.
    /// </summary>
    public bool AnalyzeAudio { get; set; } = true;

    /// <summary>Ask MusicBrainz for release years of confidently matched songs (rate limited, budgeted).</summary>
    public bool EnrichOnline { get; set; }

    /// <summary>Upper bound on online requests per run, so enabling the option can never run away.</summary>
    public int OnlineRequestBudget { get; set; } = 300;

    /// <summary>
    /// Threads to measure songs with; 0 = the service's default (about one per core, minus one). Lower it
    /// when the song library is on storage that does not like many concurrent readers.
    /// </summary>
    public int Threads { get; set; }
}

/// <summary>
/// Builds "wrapped" reports: what the last years of listening actually looked like.
/// <para>
/// The service owns the whole pipeline - read the local history, resolve which songs have a file, measure
/// the audio, aggregate, group by sound, write the report - and reports its progress after every step, so
/// a run that takes an hour is watchable and cancellable rather than a frozen window. It is deliberately
/// self contained: nothing here talks to the sync server, and no server table, DTO or shared interface
/// takes part.
/// </para>
/// </summary>
[RegisterImplementation(ServiceRegisterType.Singleton, typeof(WrappedService))]
public sealed class WrappedService
{
    /// <summary>Attempts per audio file before it counts as unreadable (a NAS can hiccup).</summary>
    const int AudioAnalysisAttempts = 2;

    /// <summary>
    /// How many songs are decoded and measured at the same time when the user did not choose.
    /// <para>
    /// Decoding plus FFT is CPU bound and parallelises almost linearly up to the core count, so the default
    /// is deliberately generous: a first run over a whole library is the one long wait in this feature, and
    /// a half-idle CPU during it is wasted time. It is capped below the core count so the machine (and the
    /// client's own UI) stays responsive, and the user can override it in the wrapped options when the
    /// bottleneck is the storage rather than the CPU - a NAS that thrashes under concurrent reads is
    /// better served by fewer threads.
    /// </para>
    /// </summary>
    static readonly int DefaultAnalysisParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    /// <summary>The thread count a run uses when the caller did not pick one (shown in the UI's "Auto").</summary>
    public static int DefaultThreads => DefaultAnalysisParallelism;

    readonly DbWrapperService dbWrapper;
    readonly SongPlaybackService songPlayback;
    readonly WrappedStore store = new();
    readonly WrappedAudioCache audioCache = new();

    public WrappedService(DbWrapperService dbWrapper, SongPlaybackService songPlayback)
    {
        this.dbWrapper = dbWrapper;
        this.songPlayback = songPlayback;
    }

    /// <summary>Raised on a worker thread whenever a run made progress.</summary>
    public event Action<WrappedProgress>? ProgressChanged;

    /// <summary>
    /// Set <c>MUSIC_PLAYER_WRAPPED_DEBUG=1</c> to have the run explain its decisions on the console (how
    /// many songs resolved to files, how many were measured, which stage was skipped and why). The client
    /// is a windowed application, so nothing it writes is visible; this is the switch that makes a run
    /// diagnosable from a terminal instead of guesswork.
    /// </summary>
    static readonly bool DebugTrace = Environment.GetEnvironmentVariable("MUSIC_PLAYER_WRAPPED_DEBUG") == "1";

    static void Trace(string message)
    {
        if (DebugTrace)
            Console.WriteLine($"[Wrapped] {message}");
    }

    /// <summary>True while a run is going on.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The reports that exist on disk, newest first.</summary>
    public List<WrappedStore.WrappedIndexEntry> ListReports() => store.LoadIndex();

    /// <summary>Reads one stored report (null when the file is gone or from an older schema).</summary>
    public WrappedReport? LoadReport(WrappedStore.WrappedIndexEntry entry) => store.Load(entry);

    /// <summary>Deletes a stored report.</summary>
    public void DeleteReport(WrappedStore.WrappedIndexEntry entry) => store.Delete(entry);

    /// <summary>
    /// How many song files the audio cache can answer for, i.e. how much of the library is already
    /// measured. Read from disk rather than from this instance's copy: the options and wrapped windows show
    /// this number, and a run may have added to the cache since the service was created - which is how the
    /// UI came to still claim "measured for 0 files" right after a full analysis run.
    /// </summary>
    public int CachedAnalyses
    {
        get
        {
            try
            {
                return new WrappedAudioCache().Count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Wrapped: could not read the audio cache: {ex.Message}");
                return audioCache.Count;
            }
        }
    }

    /// <summary>Calendar years present in the local history, newest first.</summary>
    public List<int> GetAvailableYears()
    {
        try
        {
            var years = dbWrapper.GetContext().DumpSongHistory()
                .Select(entry => entry.Date.LocalDateTime.Year)
                .Distinct()
                .OrderByDescending(year => year)
                .ToList();
            return years;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Wrapped: could not read the history years: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Computes the requested wrapped reports. Runs on the calling thread (call it from a background task);
    /// progress is reported through <see cref="ProgressChanged"/>.
    /// </summary>
    public async Task<List<WrappedReport>> ComputeAsync(WrappedOptions options, CancellationToken cancellationToken)
    {
        if (IsRunning)
            throw new InvalidOperationException("A wrapped computation is already running.");

        IsRunning = true;
        var stopwatch = Stopwatch.StartNew();
        var reports = new List<WrappedReport>();

        try
        {
            Report(WrappedStage.ReadingHistory, 0.05, "Reading the play history…", 0, 0);

            var snapshot = LoadSnapshot(cancellationToken);
            if (snapshot.Events.Count == 0)
            {
                var empty = new WrappedReport
                {
                    PeriodLabel = "All time",
                    Notes = { "The local database has no play history yet." },
                };
                store.Save(empty);
                reports.Add(empty);
                Report(WrappedStage.Completed, 1.0, "Nothing to wrap yet.", 0, 0);
                return reports;
            }

            // Resolve which songs have a file in the library, once for the whole run.
            Report(WrappedStage.ResolvingFiles, 0.15, "Resolving song files…", 0, 0);
            ResolveLibraryPaths(snapshot, cancellationToken);

            // The audio analysis is done once for the whole library and reused by every year.
            var features = new Dictionary<Guid, AudioFeatures>();
            int cacheHits = 0, analysed = 0, unreadable = 0;
            if (options.AnalyzeAudio)
            {
                var result = await AnalyzeLibraryAsync(snapshot, options, cancellationToken);
                features = result.Features;
                cacheHits = result.CacheHits;
                analysed = result.Analysed;
                unreadable = result.Unreadable;
            }

            // Online enrichment is also done once and then applied to every year.
            var enrichmentNotes = new List<string>();
            WrappedEnrichmentService? enrichment = null;
            if (options.EnrichOnline)
            {
                enrichment = new WrappedEnrichmentService();
                int matches = await EnrichAsync(snapshot, options, enrichment, enrichmentNotes, cancellationToken);
                if (matches == 0)
                    enrichmentNotes.Add("No song could be matched online; the release years are missing.");
            }

            var years = options.Years.Count > 0
                ? options.Years.Distinct().OrderByDescending(year => year).ToList()
                : snapshot.Events
                    .Select(entry => entry.Date.LocalDateTime.Year)
                    .Distinct()
                    .OrderByDescending(year => year)
                    .ToList();

            // All-time last, so the year reports are on disk even if the user cancels during it.
            var targets = new List<int?>(years.Select(year => (int?)year)) { null };
            int yearIndex = 0;

            foreach (int? year in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yearIndex++;

                string label = year is int value ? value.ToString() : "All time";
                double stageFraction = yearIndex <= years.Count ? 0.9 : 0.98;
                Report(WrappedStage.AnalyzingHistory, stageFraction,
                    $"Aggregating the listening history for {label}…", 0, 0,
                    yearIndex, targets.Count, label);

                var report = BuildReport(snapshot, features, year, stopwatch.Elapsed);
                report.SongsFromCache = cacheHits;
                report.SongsAnalysed = analysed;
                report.SongsUnreadable = unreadable;
                report.AudioAnalysisSkipped = !options.AnalyzeAudio;
                report.EnrichmentRan = options.EnrichOnline;
                report.Notes.AddRange(enrichmentNotes);
                if (audioCache.LastFailure.Length > 0)
                    report.Notes.Add($"The audio analysis could not be saved for reuse ({audioCache.LastFailure}); " +
                                     "the next run will have to measure every song again.");

                Report(WrappedStage.ClusteringSound, stageFraction, $"Grouping the songs by sound for {label}…", 0, 0,
                    yearIndex, targets.Count, label);
                PopulateAudioSections(report, snapshot, features, enrichment);

                Report(WrappedStage.BuildingReports, stageFraction, $"Writing the {label} report…", 0, 0,
                    yearIndex, targets.Count, label);
                store.Save(report);
                reports.Add(report);
            }

            stopwatch.Stop();
            foreach (var report in reports)
                report.ComputationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Written again so the stored documents carry the final runtime.
            foreach (var report in reports)
                store.Save(report);

            audioCache.Save();
            Report(WrappedStage.Completed, 1.0,
                $"Done: {reports.Count} report(s) in {FormatDuration(stopwatch.Elapsed)}.", 0, 0, targets.Count, targets.Count, "");
            return reports;
        }
        catch (OperationCanceledException)
        {
            // The audio cache holds whatever was analysed before the cancel, so the next run continues
            // instead of starting over.
            audioCache.Save();
            Report(WrappedStage.Cancelled, 0, "Cancelled. The audio analysed so far was kept for the next run.", 0, 0);
            throw;
        }
        catch (Exception ex)
        {
            audioCache.Save();
            Report(WrappedStage.Failed, 0, $"Failed: {ex.Message}", 0, 0);
            throw;
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  Reading the database
    // ---------------------------------------------------------------------------------------------

    /// <summary>Everything the analysis needs, read in one place so the pipeline below stays simple.</summary>
    sealed class LibrarySnapshot
    {
        public List<UpvotedSong> Songs { get; set; } = [];
        public List<SongHistoryEntry> Events { get; set; } = [];
        public string DisplayName { get; set; } = "";
        public string UserId { get; set; } = "";
        /// <summary>Song id -> resolved file path, for the songs whose file the library scan knows.</summary>
        public Dictionary<Guid, string> PathsBySongId { get; set; } = [];
    }

    LibrarySnapshot LoadSnapshot(CancellationToken cancellationToken)
    {
        var snapshot = new LibrarySnapshot();

        // Read through the wrapper's own accessors, so the query paths (and their indexes) stay the ones
        // the rest of the client uses.
        var context = dbWrapper.GetContext();
        try
        {
            foreach (var song in context.DumpUpvotedSongs())
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot.Songs.Add(song);
            }

            foreach (var entry in context.DumpSongHistory())
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot.Events.Add(entry);
            }
        }
        finally
        {
            context.Dispose();
        }

        using (var songDbContext = new Persistence.Database.SongDbContext())
        {
            // The first row is not necessarily the account: an abandoned local registration can sit next to
            // the real one with nothing but empty strings in it, which is how the report came out without a
            // name. Prefer the account the history belongs to, then any row that actually carries a name.
            var users = songDbContext.Users.ToList();
            var historyUserIds = snapshot.Events
                .Select(entry => entry.UserId)
                .Where(id => !string.IsNullOrEmpty(id))
                .GroupBy(id => id)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            var user = users.FirstOrDefault(candidate => historyUserIds.Contains(candidate.UserId))
                ?? users.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.UserDisplayName))
                ?? users.FirstOrDefault();

            snapshot.DisplayName = user?.UserDisplayName ?? "";
            snapshot.UserId = user?.UserId ?? "";
        }

        // The user the history actually belongs to wins over "the first user row" (an abandoned local
        // account can sit in the table next to the real one).
        var historyUser = snapshot.Events
            .GroupBy(entry => entry.UserId)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        if (historyUser != null && historyUser.Key.Length > 0)
            snapshot.UserId = historyUser.Key;

        return snapshot;
    }

    /// <summary>
    /// Maps songs to files.
    /// <para>
    /// The in-memory library list from the last scan is the <b>only</b> source, deliberately: the scan
    /// already walked the library, so it is both authoritative and free. Resolving paths by probing the
    /// filesystem per song (or even asking the database for a path and then stat-ing it) would be a
    /// round trip per song against a folder that may be a spun-down NAS - measured as a hang of many
    /// minutes before the first number is produced. A song the scan did not see simply has no file as far
    /// as the wrapped is concerned, which is exactly what "not available for playback" means anyway.
    /// </para>
    /// </summary>
    void ResolveLibraryPaths(LibrarySnapshot snapshot, CancellationToken cancellationToken)
    {
        var available = songPlayback.DumpAvailableSongs();

        // The wrapped can be asked to run before the startup library scan has happened (it is a separate
        // window and a user can open it while the client is still starting up). Without the scan list every
        // song would look like it has no file and the whole audio half would silently come out empty, so
        // the scan is run on demand here. It is the same call the startup path makes, and it is skipped
        // when the list is already populated.
        if (available.Count == 0 && !string.IsNullOrWhiteSpace(Config.Data.SongLibraryPath))
        {
            Report(WrappedStage.ResolvingFiles, 0.1,
                "Scanning the song library first, so the songs can be measured…");
            try
            {
                songPlayback.UpdateAvailableSongPaths(Config.Data.SongLibraryPath);
                available = songPlayback.DumpAvailableSongs();
            }
            catch (Exception ex)
            {
                // A library that cannot be scanned is reported, not fatal: the history half of the wrapped
                // does not need files at all.
                Console.WriteLine($"Wrapped: could not scan the song library: {ex.Message}");
                Report(WrappedStage.ResolvingFiles, 0.15, $"The song library could not be scanned: {ex.Message}");
            }
        }

        foreach (var song in available)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (song.UpvotedSongId is Guid songId && !string.IsNullOrWhiteSpace(song.FilePath))
                snapshot.PathsBySongId[songId] = song.FilePath;
        }

        Report(WrappedStage.ResolvingFiles, 0.15,
            $"{snapshot.PathsBySongId.Count} of {snapshot.Songs.Count} songs have a file in the scanned library.");
        Trace($"resolved {snapshot.PathsBySongId.Count} of {snapshot.Songs.Count} song(s); scanned library list had {available.Count} entry(ies); config library path = \"{Config.Data.SongLibraryPath}\"");
    }

    // ---------------------------------------------------------------------------------------------
    //  Audio analysis
    // ---------------------------------------------------------------------------------------------

    sealed class AudioAnalysisResult
    {
        public Dictionary<Guid, AudioFeatures> Features { get; set; } = [];
        public int CacheHits { get; set; }
        public int Analysed { get; set; }
        public int Unreadable { get; set; }
    }

    async Task<AudioAnalysisResult> AnalyzeLibraryAsync(LibrarySnapshot snapshot, WrappedOptions options, CancellationToken cancellationToken)
    {
        var result = new AudioAnalysisResult();
        var stopwatch = Stopwatch.StartNew();

        // Unique file paths, so two rows pointing at the same file are analysed once.
        var byPath = new Dictionary<string, List<Guid>>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var song in snapshot.Songs)
        {
            if (!snapshot.PathsBySongId.TryGetValue(song.SongId, out string? path) || string.IsNullOrWhiteSpace(path))
                continue;
            if (!byPath.TryGetValue(path, out var ids))
                byPath[path] = ids = [];
            ids.Add(song.SongId);
        }

        var paths = byPath.Keys.ToList();
        Trace($"audio stage: {snapshot.Songs.Count} song rows, {snapshot.PathsBySongId.Count} resolved to a file, {paths.Count} distinct file path(s), cache has {audioCache.Count} entries");
        int done = 0;
        int cacheHitCount = 0;
        int analysedCount = 0;
        int unreadableCount = 0;
        int parallelism = options.Threads > 0 ? options.Threads : DefaultAnalysisParallelism;
        Report(WrappedStage.AnalyzingAudio, 0.15,
            $"Measuring {paths.Count} song file(s) with {parallelism} thread(s)…", 0, paths.Count);
        var queue = new System.Collections.Concurrent.ConcurrentQueue<(string Path, AudioFeatures? Features, bool FromCache, bool Unreadable)>();

        await Task.Run(() =>
        {
            Parallel.ForEach(
                paths,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var cached = audioCache.TryGet(path);
                        if (cached != null)
                        {
                            Interlocked.Increment(ref cacheHitCount);
                            queue.Enqueue((path, cached, true, false));
                            return;
                        }

                        AudioFeatures? features = null;
                        for (int attempt = 1; attempt <= AudioAnalysisAttempts && features == null; attempt++)
                        {
                            try
                            {
                                features = WrappedAudioAnalyzer.AnalyzeFile(path, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception) when (attempt < AudioAnalysisAttempts)
                            {
                                // A NAS can fail a read once (spin-down, dropped mount); one retry is
                                // worth it before the song is declared unreadable.
                            }
                        }

                        if (features == null || features.AnalyzedSeconds <= 0)
                        {
                            Interlocked.Increment(ref unreadableCount);
                            queue.Enqueue((path, null, false, true));
                        }
                        else
                        {
                            Interlocked.Increment(ref analysedCount);
                            queue.Enqueue((path, features, false, false));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Wrapped: could not analyse \"{path}\": {ex.Message}");
                        Interlocked.Increment(ref unreadableCount);
                        queue.Enqueue((path, null, false, true));
                    }

                    int completed = Interlocked.Increment(ref done);
                    if (completed % 10 == 0 || completed == paths.Count)
                    {
                        var snapshot = new WrappedProgress
                        {
                            Stage = WrappedStage.AnalyzingAudio,
                            StageFraction = 0.15 + 0.5 * completed / Math.Max(1, paths.Count),
                            Message = "Measuring the songs (tempo, timbre, key, loudness)…",
                            ItemsDone = completed,
                            ItemsTotal = paths.Count,
                            CacheHits = Volatile.Read(ref cacheHitCount),
                            Analysed = Volatile.Read(ref analysedCount),
                            Unreadable = Volatile.Read(ref unreadableCount),
                        };
                        ReportProgress(snapshot);
                    }
                });
        }, cancellationToken);

        // Drain in one place: the counters and the cache flush are not thread safe by design.
        while (queue.TryDequeue(out var item))
        {
            if (item.FromCache)
                result.CacheHits++;
            else if (item.Unreadable)
                result.Unreadable++;
            else
                result.Analysed++;

            if (item.Features != null)
            {
                foreach (Guid songId in byPath[item.Path])
                    result.Features[songId] = item.Features;

                if (!item.FromCache)
                    audioCache.Store(item.Path, item.Features);
            }
        }

        audioCache.Save();
        if (audioCache.LastFailure.Length > 0)
        {
            // Surfaced into the report below: without the cache every future run re-analyses the whole
            // library, which is the one thing in this feature that costs hours.
            ReportProgress(new WrappedProgress
            {
                Stage = WrappedStage.AnalyzingAudio,
                StageFraction = 0.65,
                Message = $"Warning: the analysis could not be cached ({audioCache.LastFailure}) - a later run will measure everything again.",
                ItemsDone = paths.Count,
                ItemsTotal = paths.Count,
                CacheHits = result.CacheHits,
                Analysed = result.Analysed,
                Unreadable = result.Unreadable,
            });
        }

        Report(WrappedStage.AnalyzingAudio, 0.65,
            $"Measured {result.Features.Count} song(s) in {FormatDuration(stopwatch.Elapsed)} - {result.CacheHits} from the cache, {result.Analysed} newly measured, {result.Unreadable} unreadable.",
            paths.Count, paths.Count);

        _ = options;
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    //  Online enrichment
    // ---------------------------------------------------------------------------------------------

    async Task<int> EnrichAsync(
        LibrarySnapshot snapshot,
        WrappedOptions options,
        WrappedEnrichmentService service,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        // Only songs that were actually played in some requested period are worth a request.
        var candidates = snapshot.Songs
            .Select(song =>
            {
                var parsed = ArtistNameParser.Parse(song.Artist, song.Name);
                return (song.SongId, parsed.Artist, parsed.Title);
            })
            .Where(entry => entry.Title.Length > 0)
            .Take(Math.Max(0, options.OnlineRequestBudget))
            .ToList();

        if (candidates.Count == 0)
            return 0;

        int matched = 0;
        var (matchedCount, budgetExhausted) = await service.EnrichAsync(
            candidates,
            options.OnlineRequestBudget,
            (done, total) => Report(WrappedStage.EnrichingOnline,
                0.65 + 0.2 * done / Math.Max(1, total),
                "Looking up release information online…", done, total),
            cancellationToken);

        matched = matchedCount;
        if (budgetExhausted)
            notes.Add($"The online lookup was stopped after {options.OnlineRequestBudget} requests (MusicBrainz allows one per second); " +
                      "run it again to continue - everything already found is cached.");

        return matched;
    }

    // ---------------------------------------------------------------------------------------------
    //  Building one report
    // ---------------------------------------------------------------------------------------------

    WrappedReport BuildReport(LibrarySnapshot snapshot, Dictionary<Guid, AudioFeatures> features, int? year, TimeSpan elapsed)
    {
        DateTimeOffset periodStart;
        DateTimeOffset periodEnd;

        if (year is int value)
        {
            // The period is the calendar year; the comparison against the listening timestamps happens in
            // local time, because "2025" means the listener's 2025, not UTC's.
            periodStart = new DateTimeOffset(new DateTime(value, 1, 1, 0, 0, 0, DateTimeKind.Local));
            periodEnd = new DateTimeOffset(new DateTime(value + 1, 1, 1, 0, 0, 0, DateTimeKind.Local));
        }
        else
        {
            var dates = snapshot.Events.Select(entry => entry.Date).ToList();
            periodStart = dates.Min();
            periodEnd = dates.Max() + TimeSpan.FromSeconds(1);
        }

        var periodEvents = snapshot.Events
            .Where(entry => entry.Date >= periodStart && entry.Date < periodEnd)
            .ToList();

        var songsById = snapshot.Songs.ToDictionary(song => song.SongId);
        var inputs = new List<WrappedSongInput>();
        var eventsBySong = periodEvents
            .Where(entry => entry.SongId is not null && songsById.ContainsKey(entry.SongId.Value))
            .GroupBy(entry => entry.SongId!.Value);

        foreach (var group in eventsBySong)
        {
            var song = songsById[group.Key];
            snapshot.PathsBySongId.TryGetValue(song.SongId, out string? path);
            inputs.Add(new WrappedSongInput
            {
                SongId = song.SongId,
                Name = song.Name,
                StoredArtist = song.Artist,
                Album = song.Album,
                FilePath = path ?? "",
                // The library scan is the authority on what exists: asking the filesystem here would mean
                // one stat per song against a folder that may be a NAS.
                HasFile = !string.IsNullOrEmpty(path),
                Score = song.Score,
                Streak = song.Streak,
                TotalLikes = song.TotalLikes,
                TotalDislikes = song.TotalDislikes,
                Volume = song.Volume,
                DateAdded = song.DateAdded,
                Events = group
                    .OrderBy(entry => entry.Date)
                    .Select(entry => new WrappedEvent(entry.Date, entry.ScoreChange))
                    .ToList(),
            });
        }

        var libraryInputs = snapshot.Songs
            .Select(song => new WrappedSongInput
            {
                SongId = song.SongId,
                Name = song.Name,
                StoredArtist = song.Artist,
                Album = song.Album,
                HasFile = snapshot.PathsBySongId.ContainsKey(song.SongId),
                Score = song.Score,
                TotalLikes = song.TotalLikes,
                TotalDislikes = song.TotalDislikes,
            })
            .ToList();

        var report = new WrappedReport
        {
            UserId = snapshot.UserId,
            DisplayName = snapshot.DisplayName,
            Year = year,
            PeriodLabel = year is int reportYear ? reportYear.ToString() : "All time",
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            SongsInDatabase = snapshot.Songs.Count,
            SongsWithFiles = snapshot.PathsBySongId.Count,
            ComputationSeconds = elapsed.TotalSeconds,
        };

        WrappedHistoryAnalyzer.Analyze(
            report,
            inputs,
            libraryInputs,
            periodStart,
            periodEnd,
            periodEvents.Count,
            snapshot.Events.Count);

        _ = features;
        return report;
    }

    /// <summary>
    /// Fills the audio half of a report. Kept separate from <see cref="BuildReport"/> because it needs the
    /// feature dictionary (and therefore the audio pass), while the listening half does not.
    /// </summary>
    void PopulateAudioSections(
        WrappedReport report,
        LibrarySnapshot snapshot,
        Dictionary<Guid, AudioFeatures> features,
        WrappedEnrichmentService? enrichment)
    {
        if (features.Count == 0)
        {
            report.Notes.Add("No audio was analysed, so the sound grouping is missing (tick \"analyse the audio\" in the wrapped options).");
            return;
        }

        // Plays per song inside this period, so the clusters can say what was actually listened to.
        var periodEvents = snapshot.Events
            .Where(entry => entry.Date >= report.PeriodStart && entry.Date < report.PeriodEnd && entry.SongId is not null)
            .ToList();

        var playCounts = periodEvents
            .Where(entry => entry.SongId is not null)
            .GroupBy(entry => entry.SongId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var upvoteCounts = periodEvents
            .Where(entry => entry.SongId is not null && entry.ScoreChange > 0.0001f)
            .GroupBy(entry => entry.SongId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var downvoteCounts = periodEvents
            .Where(entry => entry.SongId is not null && entry.ScoreChange < -0.0001f)
            .GroupBy(entry => entry.SongId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var analysed = new List<WrappedAnalysedSong>();
        foreach (var song in snapshot.Songs)
        {
            if (!features.TryGetValue(song.SongId, out var songFeatures))
                continue;

            var parsed = ArtistNameParser.Parse(song.Artist, song.Name);
            snapshot.PathsBySongId.TryGetValue(song.SongId, out string? path);
            analysed.Add(new WrappedAnalysedSong
            {
                SongId = song.SongId,
                Artist = parsed.Artist,
                Title = parsed.Title.Length > 0 ? parsed.Title : song.Name,
                Album = song.Album,
                FilePath = path ?? "",
                Features = songFeatures,
                TotalLikes = song.TotalLikes,
                TotalDislikes = song.TotalDislikes,
                Score = song.Score,
                DateAdded = song.DateAdded,
                PeriodPlays = playCounts.TryGetValue(song.SongId, out int plays) ? plays : 0,
                PeriodUpvotes = upvoteCounts.TryGetValue(song.SongId, out int upvotes) ? upvotes : 0,
                PeriodDownvotes = downvoteCounts.TryGetValue(song.SongId, out int downvotes) ? downvotes : 0,
            });
        }

        WrappedAudioCrossAnalyzer.Analyze(report, analysed, periodEvents.Count, []);

        if (enrichment != null)
        {
            report.ReleaseYears = BuildReleaseYears(analysed, song => enrichment.ReleaseYearFor(song.SongId, song.Artist, song.Title));
            int matched = report.ReleaseYears.Sum(entry => entry.SongCount);
            report.Notes.Add($"Release years were found online for {matched} of {analysed.Count} analysed songs; " +
                             "the rest (private rips, soundtracks, game music) exist in no database and are described by their sound instead.");
        }

        // Listening time: only songs whose duration was measured can contribute, and the report says so.
        var measured = analysed.Where(song => song.PeriodPlays > 0 && song.Features.DurationSeconds > 0).ToList();
        if (measured.Count > 0)
        {
            double seconds = measured.Sum(song => song.PeriodPlays * song.Features.DurationSeconds);
            report.Headline.TotalListeningDays = seconds / 86400.0;
            report.Notes.Add($"Playing time is estimated from {measured.Count} songs with a measured duration " +
                             $"({report.Headline.TotalListeningDays:0.0} days of audio); the history stores events, not durations.");
        }
    }

    /// <summary>Release years looked up online for the songs of a report, grouped for the UI.</summary>
    static List<WrappedReleaseYear> BuildReleaseYears(
        IReadOnlyList<WrappedAnalysedSong> analysed,
        Func<WrappedAnalysedSong, int> releaseYearFor)
    {
        // Songs the online pass could not match simply carry no year; the section then covers only the
        // matched part of the library, which the report states in its notes.
        var withYear = analysed
            .Select(song => (Song: song, Year: releaseYearFor(song)))
            .Where(entry => entry.Year > 0)
            .ToList();

        return withYear
            .GroupBy(entry => entry.Year)
            .Select(group => new WrappedReleaseYear
            {
                Year = group.Key,
                SongCount = group.Count(),
                Plays = group.Sum(entry => entry.Song.PeriodPlays),
                DistinctArtists = group
                    .Select(entry => ArtistNameParser.ArtistKey(entry.Song.Artist))
                    .Where(key => key.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
            })
            .OrderBy(entry => entry.Year)
            .ToList();
    }

    // ---------------------------------------------------------------------------------------------
    //  Progress reporting
    // ---------------------------------------------------------------------------------------------

    void Report(
        WrappedStage stage,
        double stageFraction,
        string message,
        int itemsDone = 0,
        int itemsTotal = 0,
        int yearIndex = 0,
        int yearCount = 0,
        string yearLabel = "")
    {
        ReportProgress(new WrappedProgress
        {
            Stage = stage,
            StageFraction = Math.Clamp(stageFraction, 0, 1),
            Message = message,
            ItemsDone = itemsDone,
            ItemsTotal = itemsTotal,
            YearIndex = yearIndex,
            YearCount = yearCount,
            YearLabel = yearLabel,
        });
    }

    void ReportProgress(WrappedProgress progress)
    {
        var listener = ProgressChanged;
        if (listener == null)
            return;

        try
        {
            listener(progress);
        }
        catch (Exception ex)
        {
            // A broken listener must never take the computation down.
            Console.WriteLine($"Wrapped: progress listener threw: {ex.Message}");
        }
    }

    /// <summary>Formats a duration the way the progress label shows it.</summary>
    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
            : duration.TotalMinutes >= 1
                ? $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s"
                : $"{duration.TotalSeconds:0.0}s";
}
