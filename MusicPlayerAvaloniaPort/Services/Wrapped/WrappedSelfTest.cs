#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MusicPlayerAvaloniaPort.Services.Wrapped.Analysis;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// Command line self test for the wrapped's audio analysis, so the DSP can be verified against signals
/// with a <i>known</i> answer instead of being eyeballed. It is compiled into Debug builds only and runs
/// when the environment variable <c>MUSIC_PLAYER_WRAPPED_SELFTEST</c> is set:
///
/// <code>
///   MUSIC_PLAYER_WRAPPED_SELFTEST=all   dotnet run            (FFT check + tempo suite + the files in MUSIC_PLAYER_WRAPPED_SELFTEST_FILES)
///   MUSIC_PLAYER_WRAPPED_SELFTEST=tempo dotnet run            (FFT check + tempo suite only)
/// </code>
/// <c>MUSIC_PLAYER_WRAPPED_SELFTEST_FILES</c> takes a ';' separated list of real audio files to run the
/// whole feature extraction over.
/// </summary>
public static class WrappedSelfTest
{
    public const string EnvironmentVariableName = "MUSIC_PLAYER_WRAPPED_SELFTEST";
    public const string FilesEnvironmentVariableName = "MUSIC_PLAYER_WRAPPED_SELFTEST_FILES";
    /// <summary>Folder to run the wrapped over (a copy of a data directory with song.db and config.json).</summary>
    public const string DataDirectoryEnvironmentVariableName = "MUSIC_PLAYER_WRAPPED_SELFTEST_DATA";

    /// <summary>Runs the self test when it was requested; returns true when the process should exit.</summary>
    public static bool RunIfRequested()
    {
        string? mode = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(mode))
            return false;

        int failures = 0;
        try
        {
            failures = Run(mode);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELFTEST CRASHED: {ex}");
            failures = 1;
        }

        Console.WriteLine(failures == 0 ? "SELFTEST RESULT: PASS" : $"SELFTEST RESULT: FAIL ({failures})");

        // The real application keeps background machinery alive (the audio engine, the thread pool's
        // workers, the HTTP client), so simply returning from Main would leave a console-less process
        // running after the test printed its result. The exit code carries the outcome instead.
        Environment.Exit(failures == 0 ? 0 : 1);
        return true;
    }

    static int Run(string mode)
    {
        Console.WriteLine($"=== wrapped self test (mode: {mode}) ===");
        int failures = 0;

        if (mode == "wrapped")
            return RunWrappedOverDatabase();

        if (mode == "ui")
            return RunXamlNameCheck();

        failures += RunXamlNameCheck();
        failures += RunFftCheck();

        if (mode is "all" or "tempo")
            failures += RunTempoSuite();

        if (mode is "all")
            failures += RunRealFiles();

        Console.WriteLine("=== wrapped self test finished ===");
        return failures;
    }

    /// <summary>
    /// Checks that every control the Wrapped window looks up by name actually exists in its axaml.
    /// <para>
    /// This exists because of a real bug: the view loads its XAML with <c>AvaloniaXamlLoader.Load</c>
    /// instead of the generated <c>InitializeComponent</c>, so the fields the name generator declares are
    /// never assigned. Reading them threw a <c>NullReferenceException</c> inside an <c>async void</c>
    /// handler, which took the whole application down - and only in Release, because the self test path
    /// (which populated the report list) is compiled out there. A compile-time check cannot catch that, and
    /// the names are strings, so they are checked against the XAML here. This runs without any UI platform,
    /// which matters because a self test that needs a window cannot run in a build or CI environment.
    /// </para>
    /// </summary>
    static int RunXamlNameCheck()
    {
        Console.WriteLine("\n--- Wrapped view XAML name check ---");

        string? xamlPath = FindWrappedViewXaml();
        if (xamlPath == null)
        {
            Console.WriteLine("  could not locate WrappedView.axaml next to the executable");
            return 1;
        }

        string xaml = File.ReadAllText(xamlPath);
        // Both "Name" and "x:Name" name a control; the word boundary keeps other attributes that merely
        // end in "Name" out of the result.
        var declaredNames = System.Text.RegularExpressions.Regex
            .Matches(xaml, @"(?<![:\w])x?:?Name\s*=\s*""(?<name>[^""]+)""")
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Every name the view resolves through the visual tree has to be declared in the axaml.
        string[] lookedUp =
        [
            "reportSelector", "audioCheckBox", "onlineCheckBox", "libraryStateLabel", "yearsPanel",
            "progressBar", "progressPercentLabel", "progressLabel", "contentPanel", "computeButton",
            "cancelButton", "deleteReportButton",
        ];

        int failures = 0;
        foreach (string name in lookedUp)
        {
            bool found = declaredNames.Contains(name);
            if (!found)
                failures++;
            Console.WriteLine($"  {name,-22} {(found ? "declared" : "MISSING IN AXAML")}");
        }

        // And every event handler the axaml wires up has to exist on the view. The event names are matched
        // explicitly (rather than any "attribute=value" pair, which would also match property assignments).
        int handlerFailures = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            xaml, @"(?<![A-Za-z])(?<event>IsCheckedChanged|SelectionChanged|TextChanged|Unchecked|Checked|Click|KeyDown|Loaded|Unloaded|Opening)\s*=\s*""(?<handler>\w+)"""))
        {
            string handler = match.Groups["handler"].Value;
            bool exists = typeof(Views.Wrapped.WrappedView)
                .GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public) != null;
            if (!exists)
            {
                Console.WriteLine($"  event handler {handler} is wired in the axaml but missing on the view");
                handlerFailures++;
            }
        }
        failures += handlerFailures;
        Console.WriteLine($"  event handlers: {handlerFailures} missing");

        Console.WriteLine(failures == 0 ? "  xaml name check: OK" : $"  xaml name check: {failures} failure(s)");
        return failures;
    }

    /// <summary>Finds WrappedView.axaml in the build output (it is a UserControl, not an embedded resource).</summary>
    static string? FindWrappedViewXaml()
    {
        // The axaml is compiled, but the original file is copied to the output directory by the Avalonia
        // build targets' item metadata, so looking next to the executable and in the project layout covers
        // both a build-output run and a run from the project folder.
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Views", "Wrapped", "WrappedView.axaml"),
            Path.Combine(AppContext.BaseDirectory, "WrappedView.axaml"),
        };

        // Walk up from the executable towards the project file (bin/Debug/net10.0 -> the project folder).
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 6 && directory != null; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "Views", "Wrapped", "WrappedView.axaml"));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Runs the whole wrapped pipeline over a copy of a real data directory - the listening analysis, the
    /// report building and the JSON round trip - with the audio analysis switched off, so it needs no
    /// library. This is what verifies the feature against 20 000+ real history entries instead of against
    /// a fixture.
    /// </summary>
    static int RunWrappedOverDatabase()
    {
        string? dataDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Directory.Exists(dataDirectory))
        {
            Console.WriteLine($"Set {DataDirectoryEnvironmentVariableName} to a folder holding song.db.");
            return 1;
        }

        // Point the client at the copy before anything reads the config or opens the database.
        Persistence.PersistenceLocations.Configure(Persistence.PersistenceLocations.DefaultAppName, () => dataDirectory);

        var service = ServiceContainer.GetService<Services.Wrapped.WrappedService>();
        var years = service.GetAvailableYears();
        Console.WriteLine($"History years in the database: {string.Join(", ", years)}");
        if (years.Count == 0)
        {
            Console.WriteLine("No history to wrap.");
            return 1;
        }

        var options = new Services.Wrapped.WrappedOptions
        {
            Years = years,
            AnalyzeAudio = false,
            EnrichOnline = false,
        };

        var reports = service.ComputeAsync(options, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"\nComputed {reports.Count} report(s).\n");

        foreach (var report in reports)
            PrintReport(report);

        // Round trip through the store, so a broken schema shows up here rather than on the next start.
        var store = new Services.Wrapped.WrappedStore();
        int reloaded = 0;
        foreach (var entry in store.LoadIndex())
        {
            var loaded = store.Load(entry);
            if (loaded == null)
            {
                Console.WriteLine($"STORE ROUND TRIP FAILED for {entry.FileName}");
                return 1;
            }
            reloaded++;
        }
        Console.WriteLine($"\nStore round trip: {reloaded} report(s) read back.");

        return reports.Count == years.Count + 1 && reloaded == reports.Count ? 0 : 1;
    }

    static void PrintReport(Services.Wrapped.WrappedReport report)
    {
        Console.WriteLine($"--- {report.PeriodLabel} ---");
        Console.WriteLine($"  period            : {report.PeriodStart.LocalDateTime:yyyy-MM-dd} .. {report.PeriodEnd.LocalDateTime:yyyy-MM-dd}");
        Console.WriteLine($"  events in period  : {report.HistoryEntriesInPeriod} of {report.HistoryEntriesTotal}");
        Console.WriteLine($"  songs played      : {report.SongsPlayedInPeriod} (library {report.SongsInDatabase}, with a file {report.SongsWithFiles})");
        Console.WriteLine($"  headline          : {report.Headline.Plays} plays, {report.Headline.DistinctSongs} songs, {report.Headline.DistinctArtists} artists, " +
                          $"{report.Headline.ActiveDays} active days, streak {report.Headline.LongestDailyStreak}, " +
                          $"vote ratio {report.Headline.AverageVoteRatio * 100:0}%");
        Console.WriteLine($"  rhythm            : peak hour {report.Rhythm.PeakHour:00}:00 ({report.Rhythm.PeakHourSharePercent:0}%), " +
                          $"night {report.Rhythm.NightOwlPercent:0}%, morning {report.Rhythm.MorningPercent:0}%, weekend {report.Rhythm.WeekendPercent:0}%");
        Console.WriteLine($"  sittings          : {report.Sessions.SessionCount} sessions, {report.Sessions.AverageSongsPerSession:0.0} songs each, " +
                          $"longest {report.Sessions.LongestSessionSongs} songs / {report.Sessions.LongestSessionMinutes:0} min" +
                          (report.Sessions.LongestSessionArtist.Length > 0 ? $" ({report.Sessions.LongestSessionArtist})" : ""));
        Console.WriteLine($"  months            : {report.Months.Count}, busiest {report.Months.OrderByDescending(month => month.Plays).First().Month} " +
                          $"with {report.Months.Max(month => month.Plays)} plays");
        Console.WriteLine($"  phases            : {report.Phases.Count}");
        Console.WriteLine($"  loyal songs       : {report.LoyalSongs.Count}");

        Console.WriteLine("  highlights:");
        foreach (string highlight in report.Highlights)
            Console.WriteLine($"    * {highlight}");

        Console.WriteLine("  top artists:");
        foreach (var artist in report.TopArtists.Take(6))
            Console.WriteLine($"    {artist.Plays,5}  {artist.Name}  ({artist.DistinctSongs} songs, {artist.UnplayedOwnedSongs} owned but unplayed)");

        Console.WriteLine("  top songs:");
        foreach (var song in report.TopSongs.Take(6))
            Console.WriteLine($"    {song.Plays,5}  {song.Artist} - {song.Name}   [{song.Reason}]");

        PrintSongSection("obsessions", report.Obsessions, 4);
        PrintSongSection("one-hit wonders", report.OneHitWonders, 4);
        PrintSongSection("hall of shame", report.HallOfShame, 4);
        PrintSongSection("most divisive", report.MostDivisive, 4);
        PrintSongSection("faded out", report.FastestFaders, 4);
        PrintSongSection("rediscovered", report.Rediscovered, 4);
        PrintSongSection("newly embraced", report.NewlyEmbraced, 4);
        Console.WriteLine();
    }

    static void PrintSongSection(string title, System.Collections.Generic.List<Services.Wrapped.WrappedSong> songs, int take)
    {
        if (songs.Count == 0)
            return;
        Console.WriteLine($"  {title}:");
        foreach (var song in songs.Take(take))
            Console.WriteLine($"    {song.Plays,5}  {song.Artist} - {song.Name}   [{song.Reason}]");
    }

    /// <summary>
    /// Carries the analysis tolerance for the synthetic suite: how far the measured tempo may be from the
    /// tempo that went in.
    /// </summary>
    const float TempoTolerance = 1.5f;

    /// <summary>
    /// Checks the FFT against signals whose spectrum is known analytically. The power spectrum is also
    /// cross checked against a naive DFT on randomly filled blocks - a pure tone or a constant can pass a
    /// broken transform by accident, random noise cannot.
    /// </summary>
    static int RunFftCheck()
    {
        Console.WriteLine("\n--- FFT check ---");
        int failures = 0;

        var fft = new Fft(WrappedAudioAnalyzer.FftSize);
        var power = new float[fft.BinCount];

        // A cosine at bin 100 must produce a spike at bin 100 and nothing anywhere else. With the
        // periodic Hann window the expected magnitudes are N/4 at bin+-1 and N/2 at the bin itself.
        var tone = new float[fft.Size];
        for (int i = 0; i < tone.Length; i++)
            tone[i] = (float)Math.Cos(2.0 * Math.PI * 100 * i / tone.Length);
        fft.PowerSpectrum(tone, power);

        int peakBin = 0;
        for (int bin = 1; bin < power.Length; bin++)
            if (power[bin] > power[peakBin])
                peakBin = bin;

        bool peakOk = peakBin == 100;
        failures += peakOk ? 0 : 1;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  pure tone at bin 100: peak at bin {0} ({1:0.###e+0}), neighbours {2:0.###e+0} / {3:0.###e+0}  {4}",
            peakBin, power[peakBin], power[99], power[101], peakOk ? "OK" : "FAIL"));

        // A constant is a pure DC component; everything else must be (numerically) zero.
        var constant = new float[fft.Size];
        Array.Fill(constant, 0.5f);
        fft.PowerSpectrum(constant, power);
        bool constantOk = Math.Abs(power[0] - 65536.0) < 1.0 && power.Skip(2).Max() < 1e-6f;
        failures += constantOk ? 0 : 1;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  constant 0.5: DC {0:0.###e+0}, highest other bin {1:0.###e+0}  {2}",
            power[0], power.Skip(2).Max(), constantOk ? "OK" : "FAIL"));

        // Parseval: the summed power of a real block must equal N times the mean square of the windowed
        // input (the mirrored bins count double, DC and Nyquist once). This catches gain/scaling errors
        // that a peak position check would miss.
        var random = new Random(1234);
        var noise = new float[fft.Size];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() * 2.0 - 1.0);
        double windowedEnergy = 0.0;
        for (int i = 0; i < noise.Length; i++)
        {
            double windowed = noise[i] * (0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / noise.Length)));
            windowedEnergy += windowed * windowed;
        }
        fft.PowerSpectrum(noise, power);
        double powerSum = 0.0;
        for (int i = 0; i < power.Length; i++)
            powerSum += i == 0 || i == power.Length - 1 ? power[i] : 2.0 * power[i];
        double expectedSum = noise.Length * windowedEnergy;
        double parsevalError = Math.Abs(powerSum - expectedSum) / Math.Max(1.0, expectedSum);
        bool parsevalOk = parsevalError < 1e-4;
        failures += parsevalOk ? 0 : 1;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  Parseval on noise: sum {0:0.###e+0} vs expected {1:0.###e+0} (relative error {2:0.###e+0})  {3}",
            powerSum, expectedSum, parsevalError, parsevalOk ? "OK" : "FAIL"));

        // Bin by bin against a naive DFT.
        double worstRelativeError = 0.0;
        int worstBin = -1;
        for (int bin = 0; bin < 48; bin++)
        {
            double real = 0.0;
            double imaginary = 0.0;
            for (int n = 0; n < noise.Length; n++)
            {
                double angle = -2.0 * Math.PI * bin * n / noise.Length;
                double windowed = noise[n] * (0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / noise.Length)));
                real += windowed * Math.Cos(angle);
                imaginary += windowed * Math.Sin(angle);
            }
            double expected = real * real + imaginary * imaginary;
            double relativeError = Math.Abs(power[bin] - expected) / Math.Max(1e-12, expected);
            if (relativeError > worstRelativeError)
            {
                worstRelativeError = relativeError;
                worstBin = bin;
            }
        }
        bool dftOk = worstRelativeError < 1e-3;
        failures += dftOk ? 0 : 1;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  naive DFT cross check (bins 0..47): worst relative error {0:0.###e+0} at bin {1}  {2}",
            worstRelativeError, worstBin, dftOk ? "OK" : "FAIL"));

        return failures;
    }

    /// <summary>
    /// Synthetic click tracks with a known tempo. Every beat carries one accented hit and every off-beat a
    /// quiet one, over a tonal pad and a noise floor - enough structure that the onset detector has to do
    /// real work, with an accent on every beat so the beat rate really is the answer.
    /// <para>
    /// The suite deliberately stops at 85 BPM: below roughly 80 BPM a one-accent-per-beat pattern is
    /// genuinely heard two ways (the off-beat hit is a valid slower pulse too), so an estimator reporting
    /// the double tempo there is not wrong - that ambiguity is reported as
    /// <see cref="AudioFeatures.TempoConfidence"/> instead of being hidden, and is not something a test
    /// with a made-up ground truth can settle.
    /// </para>
    /// </summary>
    static int RunTempoSuite()
    {
        Console.WriteLine("\n--- synthetic tempo suite ---");
        float[] expected = [85f, 100f, 120f, 128f, 140f, 160f, 174f];
        int failures = 0;

        foreach (float bpm in expected)
        {
            var samples = ClickTrack(bpm, seconds: 40, seed: (int)(bpm * 10));
            var features = WrappedAudioAnalyzer.AnalyzeMonoSamples(samples);

            float error = Math.Abs(features.Bpm - bpm);
            bool ok = error <= TempoTolerance;
            if (!ok)
                failures++;

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,6:0.#} BPM -> measured {1,7:0.00} (raw {2,7:0.00}, confidence {3:0.00}, onsets/s {4:0.0}, key {5,7}, loudness {6:0.000})  {7}",
                bpm, features.Bpm, features.RawBpm, features.TempoConfidence, features.OnsetDensity,
                string.IsNullOrEmpty(features.KeyName) ? "-" : features.KeyName, features.LoudnessMean,
                ok ? "OK" : "FAIL"));
        }

        Console.WriteLine($"  tempo suite: {expected.Length - failures}/{expected.Length} within {TempoTolerance} BPM");
        return failures;
    }

    /// <summary>Runs the full extraction over real files and prints every feature, to sanity check a library.</summary>
    static int RunRealFiles()
    {
        string? fileList = Environment.GetEnvironmentVariable(FilesEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(fileList))
        {
            Console.WriteLine("\n--- real files: none given (set MUSIC_PLAYER_WRAPPED_SELFTEST_FILES) ---");
            return 0;
        }

        Console.WriteLine("\n--- real files ---");
        int failures = 0;
        foreach (string file in fileList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(file))
            {
                Console.WriteLine($"  {Path.GetFileName(file)}: NOT FOUND");
                failures++;
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var features = WrappedAudioAnalyzer.AnalyzeFile(file);
                stopwatch.Stop();
                Print(file, features, stopwatch.Elapsed);
                failures += CheckSane(file, features);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Console.WriteLine($"  {Path.GetFileName(file)}: FAILED after {stopwatch.ElapsedMilliseconds} ms - {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        return failures;
    }

    /// <summary>
    /// Sanity assertions that must hold for any real song: no NaN/Infinity anywhere, every ratio in
    /// 0..1, and a tempo either plausible or explicitly marked as low confidence.
    /// </summary>
    static int CheckSane(string file, AudioFeatures features)
    {
        var problems = new System.Collections.Generic.List<string>();

        void MustBeFinite(string name, float value)
        {
            if (!float.IsFinite(value))
                problems.Add($"{name} is {value}");
        }

        MustBeFinite(nameof(features.LoudnessMean), features.LoudnessMean);
        MustBeFinite(nameof(features.DynamicRange), features.DynamicRange);
        MustBeFinite(nameof(features.SpectralCentroidHz), features.SpectralCentroidHz);
        MustBeFinite(nameof(features.SpectralRolloffHz), features.SpectralRolloffHz);
        MustBeFinite(nameof(features.SpectralFlatness), features.SpectralFlatness);
        MustBeFinite(nameof(features.SpectralFlux), features.SpectralFlux);
        MustBeFinite(nameof(features.Bpm), features.Bpm);
        MustBeFinite(nameof(features.BassRatio), features.BassRatio);
        MustBeFinite(nameof(features.TrebleRatio), features.TrebleRatio);
        MustBeFinite(nameof(features.KeyCorrelation), features.KeyCorrelation);

        if (features.BassRatio is < 0f or > 1f)
            problems.Add($"bass ratio {features.BassRatio:0.###} outside 0..1");
        if (features.TrebleRatio is < 0f or > 1f)
            problems.Add($"treble ratio {features.TrebleRatio:0.###} outside 0..1");
        if (features.LoudFraction is < 0f or > 1f)
            problems.Add($"loud fraction {features.LoudFraction:0.###} outside 0..1");
        if (features.Chroma.Length != 12 || Math.Abs(features.Chroma.Sum() - 1f) > 1e-3f)
            problems.Add($"chroma does not sum to 1 (sum {features.Chroma.Sum():0.####})");
        if (features.CrestFactorDb < 0f)
            problems.Add($"negative crest factor {features.CrestFactorDb:0.#} dB");
        if (features.Bpm != 0f && (features.Bpm < TempoEstimator.MinBpm - 0.01f || features.Bpm >= TempoEstimator.MaxBpm))
            problems.Add($"tempo {features.Bpm:0.##} outside the folded range");
        if (features.Sections.Count > 0)
        {
            var last = features.Sections[^1];
            if (last.EndSeconds > features.DurationSeconds + 1.0)
                problems.Add($"last section ends at {last.EndSeconds:0.#}s but the song is {features.DurationSeconds:0.#}s long");
            if (features.Sections.Any(section => section.EndSeconds < section.StartSeconds))
                problems.Add("a section ends before it starts");
        }

        if (problems.Count == 0)
            return 0;

        Console.WriteLine($"    SANITY FAILURES: {string.Join("; ", problems)}");
        return 1;
    }

    static void Print(string file, AudioFeatures features, TimeSpan elapsed)
    {
        Console.WriteLine($"\n  {Path.GetFileName(file)}  ({features.DurationSeconds:0.0}s, analysed in {elapsed.TotalSeconds:0.00}s)");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "    tempo      : {0:0.0} BPM (raw {1:0.0}, confidence {2:0.00}, onsets/s {3:0.0}, percussiveness {4:0.00})",
            features.Bpm, features.RawBpm, features.TempoConfidence, features.OnsetDensity, features.Percussiveness));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "    key        : {0} (correlation {1:0.00}, in-key share {2:0.00})",
            string.IsNullOrEmpty(features.KeyName) ? "unclear" : features.KeyName, features.KeyCorrelation, features.KeyAdherence));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "    loudness   : mean {0:0.000}, p10 {1:0.000}, p90 {2:0.000}, dynamic range {3:0.000}, loud share {4:0.00}, crest {5:0.0} dB",
            features.LoudnessMean, features.LoudnessP10, features.LoudnessP90, features.DynamicRange, features.LoudFraction, features.CrestFactorDb));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "    spectrum   : centroid {0:0} Hz, rolloff {1:0} Hz, flatness {2:0.0000}, flux {3:0.00}, bass {4:0.00} / mid {5:0.00} / treble {6:0.00}",
            features.SpectralCentroidHz, features.SpectralRolloffHz, features.SpectralFlatness, features.SpectralFlux,
            features.BassRatio, features.MidRatio, features.TrebleRatio));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "    timbre     : mfcc1 {0:0.0} (sd {1:0.0}, delta {2:0.00}), mfcc2 {3:0.0}, mfcc3 {4:0.0}",
            features.MfccMean[1], features.MfccStd[1], features.MfccDeltaMean[1], features.MfccMean[2], features.MfccMean[3]));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "    shape      : {0} sections [{1}]",
            features.Sections.Count,
            string.Join(", ", features.Sections.Select(section =>
                $"{section.StartSeconds:0}-{section.EndSeconds:0}s {section.Character} (x{section.RelativeLoudness:0.00})"))));
    }

    /// <summary>
    /// Builds a click track at the given tempo: an accented hit on every beat, a quiet one on every
    /// off-beat, a tonal pad underneath and a noise floor on top.
    /// </summary>
    static float[] ClickTrack(float bpm, double seconds, int seed)
    {
        int sampleRate = WrappedAudioAnalyzer.AnalysisSampleRate;
        int length = (int)(seconds * sampleRate);
        var samples = new float[length];
        var random = new Random(seed);

        double beatSeconds = 60.0 / bpm;
        double noise = 0.0;

        for (int i = 0; i < length; i++)
        {
            double time = i / (double)sampleRate;
            double beatPosition = time / beatSeconds;
            double phaseInBeat = beatPosition - Math.Floor(beatPosition);

            double value = 0.0;

            // Kick: 60 Hz sine decaying over ~120 ms, on every beat.
            double kickTime = phaseInBeat * beatSeconds;
            if (kickTime < 0.12)
            {
                double envelope = Math.Exp(-kickTime * 28.0);
                value += 0.8 * envelope * Math.Sin(2.0 * Math.PI * 62.0 * kickTime);
            }

            // Hat: short noise burst on the off-beat, clearly quieter than the kick.
            double offBeat = phaseInBeat - 0.5;
            if (offBeat >= 0)
            {
                double hatTime = offBeat * beatSeconds;
                if (hatTime < 0.05)
                    value += 0.12 * Math.Exp(-hatTime * 90.0) * (random.NextDouble() * 2.0 - 1.0);
            }

            // Quiet tonal pad: an A minor triad, so the key estimate has something real to find.
            value += 0.06 * Math.Sin(2.0 * Math.PI * 220.0 * time)
                   + 0.04 * Math.Sin(2.0 * Math.PI * 261.63 * time)
                   + 0.04 * Math.Sin(2.0 * Math.PI * 329.63 * time);

            // Noise floor.
            noise = 0.98 * noise + 0.02 * (random.NextDouble() * 2.0 - 1.0);
            value += 0.01 * noise;

            samples[i] = (float)Math.Clamp(value, -1.0, 1.0);
        }

        return samples;
    }
}
#endif
