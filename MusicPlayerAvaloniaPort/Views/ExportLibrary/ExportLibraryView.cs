using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Helpers.Export;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.Views.ExportLibrary;

/// <summary>
/// Exports a selection of the library ("the best songs") into a folder, e.g. for a phone with little
/// storage space. This is the port of the DxMGP export chooser: thresholds for score, trend, vote ratio
/// and play chance decide which songs are taken, and the size of the resulting selection is reported while
/// the thresholds are adjusted. This port adds an optional size limit (the storage space of the device is
/// the real constraint) and can write straight into a phone connected over USB on Windows (see
/// <see cref="WindowsShellFolder"/>).
/// </summary>
public partial class ExportLibraryView : UserControl
{
    readonly DbWrapperService dbWrapper = ServiceContainer.GetService<DbWrapperService>();
    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    readonly SongChoosingService songChoosingService = ServiceContainer.GetService<SongChoosingService>();

    ExportLibraryViewModel? viewModel => DataContext as ExportLibraryViewModel;
    Window? window => TopLevel.GetTopLevel(this) as Window;
    MessageBox GetMessageBox() => new((ex) => Console.WriteLine(ex), window, this);

    // Controls (resolved when the view is loaded). Deliberately not named like the controls in the axaml -
    // the Avalonia name generator declares fields for those, but only fills them from its generated
    // InitializeComponent, which this view does not call.
    Slider? scoreSlider, streakSlider, ratioSlider, chanceSlider;
    NumericUpDown? maxSizeBox;
    TextBox? destinationBox;
    TextBlock? scoreLabel, streakLabel, ratioLabel, chanceLabel, selectionSummary, destinationState;
    TextBox? exportState;
    Button? phoneBrowseButton, startExportButton, cancelButton;
    ProgressBar? exportProgress;

    /// <summary>
    /// Every song of the library that can be exported (see <see cref="ExportCandidate"/>).
    /// </summary>
    List<ExportCandidate> candidates = [];

    // File size measurement. The sizes are needed for the size limit and for the reported total, and on a
    // slow library (NAS) measuring all files takes a moment, so the measurement runs in the background
    // while the view stays usable.
    readonly ConcurrentQueue<(ExportCandidate Song, long Size)> measuredSizes = new();
    /// <summary>Songs whose file could not be read while measuring (deleted in the meantime, ...). They are
    /// dropped from the selection: they cannot be copied anyway.</summary>
    readonly HashSet<Guid> unavailableSongs = [];
    CancellationTokenSource? sizeMeasurementCancellation;
    DispatcherTimer? sizeFlushTimer;
    int measuredFileCount;
    bool measuringSizes;

    // Export run
    CancellationTokenSource? exportCancellation;
    bool exportRunning;
    /// <summary>
    /// The folder picked with "Browse phone...". The shell folder object is kept (not just its path) because
    /// Windows cannot reopen a folder on a device by its path - copying through the picked object is the
    /// only way, and it only works while this window holds it (a restart needs a new pick).
    /// </summary>
    WindowsShellFolder.Selection? phoneDestination;

    bool suppressThresholdUpdates;
    bool loaded;

    public ExportLibraryView()
    {
        AvaloniaXamlLoader.Load(this);
        this.Loaded += ExportLibraryView_Loaded;
    }

    private async void ExportLibraryView_Loaded(object? sender, RoutedEventArgs e)
    {
        if (loaded)
            return;
        loaded = true;

        InitControls();

        // The thresholds are remembered between runs (exporting a library for a device is a repeating
        // task), so they are written when the window is closed as well as when an export starts.
        if (window is { } exportWindow)
            exportWindow.Closing += (_, _) => SaveExportSettings();

        await ReloadAsync();
    }

    void InitControls()
    {
        scoreSlider = this.GetNestedControl<Slider>("minScoreSlider");
        streakSlider = this.GetNestedControl<Slider>("minStreakSlider");
        ratioSlider = this.GetNestedControl<Slider>("minRatioSlider");
        chanceSlider = this.GetNestedControl<Slider>("minChanceSlider");
        maxSizeBox = this.GetNestedControl<NumericUpDown>("maxSizeNumericUpDown");
        destinationBox = this.GetNestedControl<TextBox>("destinationTextBox");
        scoreLabel = this.GetNestedControl<TextBlock>("minScoreLabel");
        streakLabel = this.GetNestedControl<TextBlock>("minStreakLabel");
        ratioLabel = this.GetNestedControl<TextBlock>("minRatioLabel");
        chanceLabel = this.GetNestedControl<TextBlock>("minChanceLabel");
        selectionSummary = this.GetNestedControl<TextBlock>("selectionSummaryText");
        exportState = this.GetNestedControl<TextBox>("exportStateText");
        destinationState = this.GetNestedControl<TextBlock>("destinationStateText");
        phoneBrowseButton = this.GetNestedControl<Button>("browsePhoneButton");
        startExportButton = this.GetNestedControl<Button>("exportButton");
        cancelButton = this.GetNestedControl<Button>("cancelExportButton");
        exportProgress = this.GetNestedControl<ProgressBar>("exportProgressBar");

        // Threshold changes re-filter the in-memory list, so they are wired here instead of in the axaml:
        // the initial values (from the config) are set while suppressThresholdUpdates is active.
        scoreSlider.ValueChanged += (_, _) => OnThresholdChanged();
        streakSlider.ValueChanged += (_, _) => OnThresholdChanged();
        ratioSlider.ValueChanged += (_, _) => OnThresholdChanged();
        chanceSlider.ValueChanged += (_, _) => OnThresholdChanged();
        maxSizeBox.ValueChanged += (_, _) => OnThresholdChanged();

        // Only Windows needs the shell folder picker: an MTP phone has no drive letter there, while Linux
        // mounts it as a normal folder.
        phoneBrowseButton.IsVisible = OperatingSystem.IsWindows();
        destinationState.Text = OperatingSystem.IsWindows()
            ? "A normal folder (a drive, an SD card), or a folder on a phone connected over USB via \"Browse phone...\"."
            : "A normal folder - on Linux a connected phone is mounted like any other device (usually below /run/user/<id>/gvfs/), so pick its Music folder here.";

        sizeFlushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, Dispatcher.UIThread);
        sizeFlushTimer.Tick += (_, _) => FlushMeasuredSizes();
    }

    // ---------- Loading ----------

    async Task ReloadAsync()
    {
        StopSizeMeasurement();

        SetExportState("Loading songs...");
        candidates = await Task.Run(BuildCandidates);
        unavailableSongs.Clear();

        ConfigureThresholdControls();
        UpdateThresholdLabels();
        UpdateSelection();
        StartSizeMeasurement();
    }

    /// <summary>
    /// Collects the songs that can be exported. Runs in the background: both the database and - on a NAS -
    /// the file lookups are slow.
    ///
    /// The list is built from the files of the last library scan, not from the database rows: a row whose
    /// file is not in the library cannot be exported, so it has no business being in this view at all (and
    /// asking the playback service for one id at a time would scan the whole library per song).
    /// </summary>
    List<ExportCandidate> BuildCandidates()
    {
        using var dbContext = dbWrapper.GetContext();

        Dictionary<Guid, UpvotedSong> rowsById = [];
        foreach (UpvotedSong row in dbContext.DumpUpvotedSongs())
            rowsById.TryAdd(row.SongId, row);

        // One pass over the choosing data structure for all songs (a per-song lookup would scan its ~76k
        // entries once per song, see SongChoosingService.GetSongChoosingChances).
        Dictionary<Guid, float> chances;
        try
        {
            chances = songChoosingService.GetSongChoosingChances();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the play chances: {ex.Message}");
            chances = [];
        }

        List<ExportCandidate> result = [];
        foreach (AvailableSong availableSong in songPlaybackService.DumpAvailableSongs())
        {
            // Files the scan could not match to a row are not registered yet: they have no score and are not
            // what this view exports.
            if (availableSong.UpvotedSongId is not Guid songId || !rowsById.TryGetValue(songId, out UpvotedSong? song))
                continue;

            // Infinite ratio for songs without a single dislike: the DxMGP export treated those as the best
            // possible ratio, so a ratio threshold never filters them out.
            float voteRatio = song.TotalDislikes > 0 ? song.TotalLikes / (float)song.TotalDislikes : float.PositiveInfinity;
            float playChancePercent = chances.TryGetValue(songId, out float chance) ? chance * 100 : 0;

            // Platform path: the library root is configured with forward slashes and the scan mixes in
            // backslashes, and the shell refuses such a path as a copy source (see WindowsShellFolder).
            string filePath;
            try
            {
                filePath = Path.GetFullPath(availableSong.FilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipping \"{availableSong.FilePath}\": {ex.Message}");
                continue;
            }

            result.Add(new ExportCandidate(songId, filePath, song.Score, song.Streak, voteRatio, playChancePercent));
        }

        return result;
    }

    /// <summary>
    /// Sets the slider ranges from the loaded songs and applies the persisted thresholds (see
    /// <see cref="ConfigData.ExportMinScore"/>).
    /// </summary>
    void ConfigureThresholdControls()
    {
        suppressThresholdUpdates = true;
        try
        {
            float minScore = candidates.Count > 0 ? candidates.Min(song => song.Score) : 0;
            float maxScore = candidates.Count > 0 ? candidates.Max(song => song.Score) : 0;
            ConfigureSlider(scoreSlider!, minScore, maxScore, Config.Data.ExportMinScore, snapToWholeNumbers: false);

            int minStreak = candidates.Count > 0 ? candidates.Min(song => song.Streak) : 0;
            int maxStreak = candidates.Count > 0 ? candidates.Max(song => song.Streak) : 0;
            ConfigureSlider(streakSlider!, minStreak, maxStreak, Config.Data.ExportMinStreak, snapToWholeNumbers: true);

            // Songs without dislikes have an infinite ratio; they are not part of the slider range.
            float maxRatio = candidates.Count > 0 ? candidates.Where(song => float.IsFinite(song.VoteRatio)).Select(song => song.VoteRatio).DefaultIfEmpty(0).Max() : 0;
            ConfigureSlider(ratioSlider!, 0, maxRatio, Config.Data.ExportMinVoteRatio, snapToWholeNumbers: false);

            float maxChance = candidates.Count > 0 ? candidates.Max(song => song.PlayChancePercent) : 0;
            ConfigureSlider(chanceSlider!, 0, maxChance, Config.Data.ExportMinPlayChancePercent, snapToWholeNumbers: false);

            maxSizeBox!.Value = (decimal)Math.Max(0, Config.Data.ExportMaxSizeMb ?? 0);

            // Only on the first load: a Refresh must not throw away a destination the user just picked
            // (a device folder cannot be restored from the config, see phoneDestination).
            if (string.IsNullOrEmpty(destinationBox!.Text))
                destinationBox.Text = Config.Data.ExportDestinationPath ?? "";
        }
        finally
        {
            suppressThresholdUpdates = false;
        }
    }

    /// <summary>
    /// Gives a threshold slider the range of the loaded data and puts it on the persisted or the lowest
    /// value. Whole-number sliders (the trend) snap to their ticks, so what the label shows is exactly what
    /// filters; the other thresholds are fractional and only step by a fraction of their range.
    /// </summary>
    static void ConfigureSlider(Slider slider, double dataMinimum, double dataMaximum, double? persistedValue, bool snapToWholeNumbers)
    {
        double minimum = Math.Floor(dataMinimum);
        double maximum = Math.Max(minimum + 1, Math.Ceiling(dataMaximum));
        slider.Minimum = minimum;
        slider.Maximum = maximum;

        double step = snapToWholeNumbers ? 1 : (maximum - minimum) / 200;
        slider.TickFrequency = step;
        slider.SmallChange = step;
        slider.IsSnapToTickEnabled = snapToWholeNumbers;

        slider.Value = Math.Clamp(persistedValue ?? minimum, minimum, maximum);
    }

    void UpdateThresholdLabels()
    {
        scoreLabel!.Text = $"Minimal Score: {scoreSlider!.Value:0.##}";
        streakLabel!.Text = $"Minimal Trend: {streakSlider!.Value:0}";
        ratioLabel!.Text = $"Minimal Vote Ratio: {ratioSlider!.Value:0.##}";
        chanceLabel!.Text = $"Minimal Play Chance: {chanceSlider!.Value:0.###} %";
    }

    void OnThresholdChanged()
    {
        if (suppressThresholdUpdates || !loaded)
            return;

        UpdateThresholdLabels();
        UpdateSelection();
    }

    ExportThresholds ReadThresholds() => new(
        (float)scoreSlider!.Value,
        (int)streakSlider!.Value,
        (float)ratioSlider!.Value,
        (float)chanceSlider!.Value,
        (double)maxSizeBox!.Value.GetValueOrDefault());

    // ---------- Selection ----------

    void UpdateSelection()
    {
        var thresholds = ReadThresholds();

        // Songs whose file turned out to be unreadable while measuring are dropped: they cannot be copied.
        List<ExportCandidate> selected = LibraryExportSelection.SelectSongs(
            candidates.Where(song => !unavailableSongs.Contains(song.SongId)),
            thresholds);

        if (viewModel != null)
            viewModel.SelectedSongs = selected;

        UpdateSummaryText();
        startExportButton!.IsEnabled = !exportRunning && !measuringSizes && selected.Count > 0;
    }

    void UpdateSummaryText()
    {
        List<ExportCandidate> selected = viewModel?.SelectedSongs ?? [];
        long totalBytes = LibraryExportSelection.TotalSizeBytes(selected);

        string summary;
        if (candidates.Count == 0)
            summary = "The music library has no songs to export - is the library folder set and available?";
        else if (unavailableSongs.Count == candidates.Count)
        {
            // Every file failed the size measurement: the library is most likely not available right now
            // (an unmounted drive or NAS), which is worth saying instead of showing an empty selection.
            summary = $"None of the {candidates.Count} song files could be read - is the music library available?";
        }
        else
        {
            summary = selected.Count == 0
                ? "No song passes these thresholds"
                : $"{selected.Count} of {candidates.Count} songs selected ({ExportCandidate.FormatSize(totalBytes)})";
        }

        decimal maxSizeMb = maxSizeBox!.Value.GetValueOrDefault();
        if (maxSizeMb > 0)
            summary += $" - limited to {maxSizeMb:0} MB";

        selectionSummary!.Text = summary;
    }

    // ---------- File size measurement ----------

    void StartSizeMeasurement()
    {
        StopSizeMeasurement();

        if (candidates.Count == 0)
        {
            SetExportState("");
            return;
        }

        var cancellation = new CancellationTokenSource();
        sizeMeasurementCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        measuringSizes = true;
        measuredFileCount = 0;
        measuredSizes.Clear();

        List<ExportCandidate> songs = candidates;
        SetExportState($"Measuring the size of {songs.Count} song files...");

        _ = Task.Run(() =>
        {
            try
            {
                // A few files at a time: the library can be a NAS, where every stat is a round trip.
                Parallel.ForEach(
                    songs,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
                    song =>
                    {
                        long size;
                        try
                        {
                            size = new FileInfo(song.FilePath).Length;
                        }
                        catch
                        {
                            size = -1; // Gone or unreadable - reported by the flush below
                        }

                        measuredSizes.Enqueue((song, size));
                        Interlocked.Increment(ref measuredFileCount);
                    });
            }
            catch (OperationCanceledException)
            {
                // The view was reloaded or closed: the partial result is not used any more.
            }

            Dispatcher.UIThread.Post(() => FinishSizeMeasurement(cancellation));
        });

        sizeFlushTimer!.Start();
    }

    void StopSizeMeasurement()
    {
        sizeMeasurementCancellation?.Cancel();
        sizeMeasurementCancellation = null;
        sizeFlushTimer?.Stop();
        measuringSizes = false;
    }

    void FlushMeasuredSizes()
    {
        bool anyMeasured = false;
        while (measuredSizes.TryDequeue(out (ExportCandidate Song, long Size) measured))
        {
            anyMeasured = true;
            if (measured.Size < 0)
                unavailableSongs.Add(measured.Song.SongId);
            else
                measured.Song.SizeBytes = measured.Size;
        }

        if (measuringSizes)
        {
            SetExportState($"Measuring the size of the song files... {Volatile.Read(ref measuredFileCount)}/{candidates.Count}");
            if (anyMeasured)
                UpdateSummaryText();
            return;
        }

        // The measurement finished: the reported sizes are final, so refresh the grid (its Size column) and
        // the summary, and let the size limit apply.
        if (anyMeasured)
            UpdateSelection();
    }

    /// <summary>
    /// Called on the UI thread once the background measurement of <paramref name="measurement"/> finished.
    /// A reload can have started a newer measurement in the meantime - that one owns the state then.
    /// </summary>
    void FinishSizeMeasurement(CancellationTokenSource measurement)
    {
        if (!ReferenceEquals(sizeMeasurementCancellation, measurement))
            return;

        FlushMeasuredSizes();
        measuringSizes = false;
        sizeFlushTimer?.Stop();
        SetExportState("");
        UpdateSelection();
    }

    void SetExportState(string text) => exportState!.Text = text;

    // ---------- Destination ----------

    async void BrowseDestinationButton_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null)
            return;

        string currentDestination = destinationBox!.Text?.Trim() ?? "";
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the folder the songs are exported into",
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(currentDestination)
                ? await storageProvider.TryGetFolderFromPathAsync(currentDestination)
                : null
        });

        if (folders == null || folders.Count == 0)
            return;

        // LocalPath (not AbsolutePath, which would turn "D:\Music" into "/D:/Music"): the OS path is what
        // the export copies into.
        string pickedPath = folders[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(pickedPath))
            pickedPath = folders[0].Path.AbsolutePath;

        destinationBox.Text = pickedPath;
        phoneDestination = null;
        destinationState!.Text = "Folder selected.";
    }

    async void BrowsePhoneButton_Click(object? sender, RoutedEventArgs e)
    {
        IntPtr ownerHandle = window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        // The dialog runs on the device copy's own thread: the shell objects it returns have to live in the
        // apartment the copies run in (see WindowsShellFolder), so this is awaited instead of blocking.
        WindowsShellFolder.BrowseResult browse = await WindowsShellFolder.BrowseForFolderAsync(ownerHandle, "Select the folder on the device");

        if (browse.Selection == null)
        {
            if (!string.IsNullOrEmpty(browse.Error))
                GetMessageBox().Show("Selecting a device folder failed", browse.Error);
            return;
        }

        phoneDestination = browse.Selection;
        destinationBox!.Text = browse.Selection.Path;
        destinationState!.Text = $"Device folder selected{(browse.Selection.Title.Length > 0 ? $" (\"{browse.Selection.Title}\")" : "")}. "
            + "Copying over the shell (MTP) is much slower than copying to a drive - the progress below keeps you posted, and the "
            + "diagnostics log records what the shell did with every file. "
            + "Pick the folder again after restarting the player: Windows does not allow reopening a device folder by its path.";
    }

    /// <summary>
    /// Turns the destination into the copier of the export run: the shell folder object picked with
    /// "Browse phone..." for a device, the (resolvable) shell path for a typed one, and a filesystem copier
    /// otherwise.
    /// </summary>
    bool TryPrepareDestination(string destination, out LibraryExportCopier.CopyFile copyFile, out string? error)
    {
        copyFile = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(destination))
        {
            error = "Pick a destination folder first.";
            return false;
        }

        // The folder picked in the device dialog is used through the shell object the dialog returned - its
        // path cannot be resolved again (see WindowsShellFolder).
        if (phoneDestination != null && destination == phoneDestination.Path)
        {
            copyFile = WindowsShellFolder.CreateCopier(phoneDestination);
            return true;
        }

        if (WindowsShellFolder.IsShellPath(destination))
        {
            if (!OperatingSystem.IsWindows())
            {
                error = "This shell path can only be used on Windows.";
                return false;
            }

            copyFile = WindowsShellFolder.CreateCopier(destination);
            return true;
        }

        if (IsInsideSongLibrary(destination))
        {
            error = "The destination is inside your music library - exporting the library into itself would copy every song onto its own file.";
            return false;
        }

        try
        {
            if (!Directory.Exists(destination))
            {
                Directory.CreateDirectory(destination);
                destinationState!.Text = $"Created the destination folder \"{destination}\".";
            }
        }
        catch (Exception ex)
        {
            error = $"The destination folder \"{destination}\" does not exist and could not be created:\n{ex.Message}\n\n"
                + "If this is a phone connected over USB, Windows does not give it a drive letter - use \"Browse phone...\" instead.";
            return false;
        }

        copyFile = LibraryExportCopier.CreateFileSystemCopier(destination);
        return true;
    }

    /// <summary>True when the destination folder is the music library itself or inside it.</summary>
    static bool IsInsideSongLibrary(string destination)
    {
        string? libraryPath = Config.Data.SongLibraryPath;
        if (string.IsNullOrWhiteSpace(libraryPath))
            return false;

        try
        {
            char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
            string library = Path.GetFullPath(libraryPath).TrimEnd(separators) + Path.DirectorySeparatorChar;
            string destinationPath = Path.GetFullPath(destination).TrimEnd(separators) + Path.DirectorySeparatorChar;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return destinationPath.StartsWith(library, comparison);
        }
        catch
        {
            return false; // An unparsable path is rejected by the copy itself
        }
    }

    // ---------- Export ----------

    async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (exportRunning || measuringSizes)
            return;

        List<ExportCandidate> selected = viewModel?.SelectedSongs ?? [];
        if (selected.Count == 0)
        {
            GetMessageBox().Show("Nothing to export", "No song passes the current thresholds.");
            return;
        }

        string destination = destinationBox!.Text?.Trim() ?? "";
        if (!TryPrepareDestination(destination, out LibraryExportCopier.CopyFile copyFile, out string? error))
        {
            GetMessageBox().Show("Export failed", error!);
            return;
        }

        SaveExportSettings();

        ExportLog.StartRun($"Export of {selected.Count} song(s) to \"{AbbreviateDestination(destination)}\"");

        exportCancellation = new CancellationTokenSource();
        SetExportRunning(true);

        var progress = new Progress<ExportCopyProgress>(OnExportProgress);
        ExportCopyResult result;
        try
        {
            // Deliberately NOT wrapped in Task.Run: a copy to a phone has to run on this (the UI) thread,
            // which is the STA with the message pump the shell needs (see WindowsShellFolder). The copier
            // itself moves the actual file IO of a normal folder off this thread.
            result = await LibraryExportCopier.CopyAsync(selected, copyFile, progress, exportCancellation.Token);
        }
        finally
        {
            SetExportRunning(false);
        }

        string summary = BuildExportSummary(result, destination);

        // The exported folder is a library, so it gets the library's state file as well (which account it
        // belongs to + the migration high-water mark) - see LibraryExportStateFile.
        if (result.Copied + result.Skipped > 0)
            summary += $"\n{await LibraryExportStateFile.CopyIntoExportAsync(copyFile, Config.Data.SongLibraryPath)}";

        SetExportState(summary);

        if (result.Errors.Count > 0)
        {
            string shownErrors = string.Join("\n", result.Errors.Take(10));
            if (result.Errors.Count > 10)
                shownErrors += $"\n... and {result.Errors.Count - 10} more";

            // The copy engine of the shell reports nothing back when a device refuses the files, so point the
            // user at the diagnostics right away: that log is what tells a device problem from an app problem.
            GetMessageBox().Show(
                "Export finished with errors",
                $"{summary}\n\n{shownErrors}\n\nDiagnostics were written to:\n{ExportLog.Path}\n\n"
                    + "(The \"Diagnostics\" button in the export window shows and copies them.)",
                width: 620,
                height: 320);
        }
    }

    /// <summary>Shows the log of the last export (a copyable dialog - it is meant to be handed over).</summary>
    private void DiagnosticsButton_Click(object? sender, RoutedEventArgs e)
    {
        string log = ExportLog.ReadTail();
        GetMessageBox().Show(
            "Export diagnostics",
            log.Length == 0
                ? "No export was run yet in this session."
                : $"Log file: {ExportLog.Path}\n\n{log}",
            width: 900,
            height: 620);
    }

    /// <summary>Shortens a destination for the log header: a device path is a few hundred characters.</summary>
    static string AbbreviateDestination(string destination) =>
        destination.Length <= 120 ? destination : destination[..60] + "…" + destination[^60..];

    void OnExportProgress(ExportCopyProgress progress)
    {
        exportProgress!.Value = progress.Total <= 0 ? 0 : 100.0 * progress.Processed / progress.Total;

        string status = $"{progress.Processed}/{progress.Total} - {progress.CurrentFileName}\n"
            + $"Copied {progress.Copied}, already on the device {progress.Skipped}, failed {progress.Failed}";

        // Show why a song failed right away instead of only in the summary at the end.
        if (!string.IsNullOrEmpty(progress.LastError))
            status += $"\nLast error: {progress.LastError}";

        SetExportState(status);
    }

    static string BuildExportSummary(ExportCopyResult result, string destination)
    {
        string summary = result.Cancelled
            ? $"Export cancelled after {result.Copied + result.Skipped} of the songs - "
            : "Export finished - ";
        summary += $"{result.Copied} songs copied ({ExportCandidate.FormatSize(result.CopiedBytes)}), "
            + $"{result.Skipped} were already there, {result.Failed} failed.";

        if (result.Aborted)
            summary += $"\nThe export was stopped because {LibraryExportCopier.MaxConsecutiveFailures} songs in a row failed - "
                + "the destination does not seem to accept the files (see the errors below).";

        // Only worth saying when something was actually handed to the device.
        return WindowsShellFolder.IsShellPath(destination) && result.Copied > 0
            ? summary + "\nGive the device a moment to finish writing before unplugging it."
            : summary;
    }

    void CancelExportButton_Click(object? sender, RoutedEventArgs e)
    {
        exportCancellation?.Cancel();
        cancelButton!.IsEnabled = false;
        SetExportState("Cancelling after the current song...");
    }

    void SetExportRunning(bool running)
    {
        exportRunning = running;
        startExportButton!.IsEnabled = !running && !measuringSizes && (viewModel?.SelectedSongs.Count ?? 0) > 0;
        cancelButton!.IsVisible = running;
        cancelButton.IsEnabled = true;
        exportProgress!.IsVisible = running;
        exportProgress.Value = 0;
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (exportRunning)
            return;

        await ReloadAsync();
    }

    void SaveExportSettings()
    {
        // A device folder is deliberately NOT persisted: Windows cannot reopen it from its path, so a
        // stored path would only produce a failing export the next time the player starts. The thresholds
        // are remembered, and the device folder is picked again with "Browse phone...".
        string destination = destinationBox?.Text?.Trim() ?? "";
        Config.Data.ExportDestinationPath = WindowsShellFolder.IsShellPath(destination) ? null : destination;
        Config.Data.ExportMinScore = (float)(scoreSlider?.Value ?? 0);
        Config.Data.ExportMinStreak = (int)(streakSlider?.Value ?? 0);
        Config.Data.ExportMinVoteRatio = (float)(ratioSlider?.Value ?? 0);
        Config.Data.ExportMinPlayChancePercent = (float)(chanceSlider?.Value ?? 0);
        Config.Data.ExportMaxSizeMb = (double)(maxSizeBox?.Value ?? 0);
        Config.Save();
    }
}
