using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Wrapped;

namespace MusicPlayerAvaloniaPort.Views.Wrapped;

/// <summary>
/// The "wrapped" window: pick the years, watch the computation run, read the reports.
/// <para>
/// The view builds its report panels in code instead of binding a fixed XAML template, because a wrapped
/// is a variable number of sections with a variable number of rows each and the whole point is that a
/// figure can be explained (each row carries the reason it is there). A report is expensive to compute and
/// therefore only ever rendered - never recomputed while the user is looking at it.
/// </para>
/// </summary>
public partial class WrappedView : UserControl
{
    readonly WrappedService wrappedService = ServiceContainer.GetService<WrappedService>();

    // The controls are resolved by name after the XAML is loaded (see the properties below). The axaml
    // names them for the Avalonia name generator, but those generated fields are only filled by the
    // generated InitializeComponent() - which this view does not call (it uses AvaloniaXamlLoader.Load
    // like the other windows) - so reading the generated fields directly is a NullReferenceException at
    // runtime. Exactly that is how the "Compute wrapped" button came to close the whole application
    // instead of doing anything. The properties below are therefore resolved through the visual tree, and
    // they are deliberately *not* named like the controls in the axaml (the name generator would then
    // declare a conflicting member).
    ComboBox? ReportSelector => this.GetNestedControl<ComboBox>("reportSelector");
    CheckBox? AudioCheckBox => this.GetNestedControl<CheckBox>("audioCheckBox");
    CheckBox? OnlineCheckBox => this.GetNestedControl<CheckBox>("onlineCheckBox");
    TextBlock? LibraryStateLabel => this.GetNestedControl<TextBlock>("libraryStateLabel");
    ItemsControl? YearsPanel => this.GetNestedControl<ItemsControl>("yearsPanel");
    ProgressBar? ProgressBar => this.GetNestedControl<ProgressBar>("progressBar");
    TextBlock? ProgressPercentLabel => this.GetNestedControl<TextBlock>("progressPercentLabel");
    TextBlock? ProgressLabel => this.GetNestedControl<TextBlock>("progressLabel");
    StackPanel? ContentPanel => this.GetNestedControl<StackPanel>("contentPanel");
    Button? ComputeButton => this.GetNestedControl<Button>("computeButton");
    Button? CancelButton => this.GetNestedControl<Button>("cancelButton");
    Button? DeleteReportButton => this.GetNestedControl<Button>("deleteReportButton");

    /// <summary>The reports on disk, as the selector shows them.</summary>
    List<WrappedStore.WrappedIndexEntry> reports = [];
    /// <summary>The years the user ticked for the next run.</summary>
    readonly HashSet<int> selectedYears = [];
    CancellationTokenSource? cancellation;

    public WrappedView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    void OnLoaded(object? sender, RoutedEventArgs e)
    {
        wrappedService.ProgressChanged -= OnProgress;
        wrappedService.ProgressChanged += OnProgress;

        UpdateLibraryState();
        ReloadReports(selectNewest: true);
    }

    void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        wrappedService.ProgressChanged -= OnProgress;
    }

    // ---------------------------------------------------------------------------------------------
    //  Report list
    // ---------------------------------------------------------------------------------------------

    void UpdateLibraryState()
    {
        if (LibraryStateLabel == null)
            return;

        var years = wrappedService.GetAvailableYears();
        if (years.Count == 0)
        {
            LibraryStateLabel.Text = "The local database has no play history yet - play some songs first.";
            return;
        }

        bool libraryConfigured = !string.IsNullOrWhiteSpace(Config.Data.SongLibraryPath);
        LibraryStateLabel.Text =
            $"History available for {years.Count} year(s): {string.Join(", ", years)}. " +
            $"Audio already measured for {wrappedService.CachedAnalyses} song file(s) - a second run reuses them. " +
            (libraryConfigured
                ? "Each selected year becomes its own report, plus one report over everything."
                : "No song library is configured, so the audio cannot be measured (the history part still works).");

        BuildYearCheckBoxes(years);
    }

    void BuildYearCheckBoxes(List<int> years)
    {
        if (YearsPanel == null)
            return;

        // Default: everything. The listener almost always wants the whole picture, and unticking is cheap.
        if (selectedYears.Count == 0)
            foreach (int year in years)
                selectedYears.Add(year);

        // Drop selections of years that disappeared (a history pull can remove rows).
        selectedYears.RemoveWhere(year => !years.Contains(year));

        YearsPanel.ItemsSource = null;
        YearsPanel.ItemsSource = years
            .OrderByDescending(year => year)
            .Select(year =>
            {
                var checkBox = new CheckBox
                {
                    Content = year.ToString(),
                    IsChecked = selectedYears.Contains(year),
                    Margin = new Thickness(0, 0, 8, 0),
                };
                checkBox.IsCheckedChanged += (_, _) =>
                {
                    if (checkBox.IsChecked == true)
                        selectedYears.Add(year);
                    else
                        selectedYears.Remove(year);
                };
                return checkBox;
            })
            .ToList();
    }

    void ReloadReports(bool selectNewest)
    {
        reports = wrappedService.ListReports();
        ReportSelector.ItemsSource = reports
            .Select(entry => $"{entry.PeriodLabel}  -  computed {entry.ComputedAt.LocalDateTime:yyyy-MM-dd HH:mm}  ({entry.Plays} plays)")
            .ToList();
        if (reports.Count > 0 && selectNewest)
            ReportSelector.SelectedIndex = 0;
        else if (reports.Count == 0)
            ShowPlaceholder();

        DeleteReportButton.IsEnabled = reports.Count > 0;
    }

    void ShowPlaceholder()
    {
        ContentPanel.Children.Clear();
        ContentPanel.Children.Add(new TextBlock
        {
            Text = "No wrapped yet. Tick the years you want and press \"Compute wrapped\".",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#99FFFFFF")),
        });
    }

    void ReportSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = ReportSelector.SelectedIndex;
        if (index < 0 || index >= reports.Count)
            return;

        var report = wrappedService.LoadReport(reports[index]);
        if (report == null)
        {
            ContentPanel.Children.Clear();
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "This report cannot be read (it may have been written by an older version). Compute it again.",
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        RenderReport(report);
    }

    void RefreshReportsButton_Click(object? sender, RoutedEventArgs e) => ReloadReports(selectNewest: false);

    void DeleteReportButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = ReportSelector.SelectedIndex;
        if (index < 0 || index >= reports.Count)
            return;

        wrappedService.DeleteReport(reports[index]);
        ReloadReports(selectNewest: true);
    }

    void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string directory = PersistenceLocations.WrappedDirectory;
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo { FileName = "xdg-open", Arguments = $"\"{directory}\"", UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open the wrapped folder: {ex.Message}");
        }
    }

    async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        int index = ReportSelector.SelectedIndex;
        if (index < 0 || index >= reports.Count)
            return;

        var report = wrappedService.LoadReport(reports[index]);
        if (report == null)
            return;

        try
        {
            // A wrapped is long enough that the clipboard would be awkward; it is written next to the
            // reports instead, which is also where a user would go looking for it.
            Directory.CreateDirectory(PersistenceLocations.WrappedDirectory);
            string fileName = $"{(report.Year is int year ? year.ToString() : "all-time")} wrapped.txt";
            string path = Path.Combine(PersistenceLocations.WrappedDirectory, fileName);
            await File.WriteAllTextAsync(path, RenderReportAsText(report));

            ProgressLabel.Text = $"Written to {path}";
        }
        catch (Exception ex)
        {
            ProgressLabel.Text = $"Could not write the report: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  Year selection shortcuts
    // ---------------------------------------------------------------------------------------------

    void SelectAllYearsButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (int year in wrappedService.GetAvailableYears())
            selectedYears.Add(year);
        UpdateLibraryState();
    }

    void SelectRecentYearsButton_Click(object? sender, RoutedEventArgs e)
    {
        selectedYears.Clear();
        foreach (int year in wrappedService.GetAvailableYears().Take(3))
            selectedYears.Add(year);
        UpdateLibraryState();
    }

    // ---------------------------------------------------------------------------------------------
    //  Running the computation
    // ---------------------------------------------------------------------------------------------

    async void ComputeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (wrappedService.IsRunning)
            return;

        var years = selectedYears.OrderByDescending(year => year).ToList();
        if (years.Count == 0)
        {
            ProgressLabel.Text = "Select at least one year (or press \"All\").";
            return;
        }

        var options = new WrappedOptions
        {
            Years = years,
            AnalyzeAudio = AudioCheckBox.IsChecked == true,
            EnrichOnline = OnlineCheckBox.IsChecked == true,
        };

        cancellation = new CancellationTokenSource();
        SetRunning(true);

        try
        {
            await Task.Run(() => wrappedService.ComputeAsync(options, cancellation.Token), cancellation.Token);
            ReloadReports(selectNewest: true);
        }
        catch (OperationCanceledException)
        {
            // The service already reported the cancellation; the partial audio cache is kept.
        }
        catch (Exception ex)
        {
            ProgressLabel.Text = $"The wrapped computation failed: {ex.Message}";
        }
        finally
        {
            SetRunning(false);
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        ProgressLabel.Text = "Cancelling… (the audio measured so far is kept)";
        cancellation?.Cancel();
    }

    void SetRunning(bool running)
    {
        ComputeButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        ReportSelector.IsEnabled = !running;
    }

    void OnProgress(WrappedProgress progress)
    {
        // Progress arrives from the computation thread, the controls belong to the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = progress.OverallFraction;
            ProgressPercentLabel.Text = $"{progress.OverallFraction * 100:0}%";

            string prefix = progress.YearCount > 0 ? $"[{progress.YearLabel}] " : "";
            string items = progress.ItemsTotal > 0 || progress.ItemsDone > 0
                ? $"  ({progress.AudioSummary})"
                : "";
            ProgressLabel.Text = $"{prefix}{progress.Message}{items}";
        });
    }

    // ---------------------------------------------------------------------------------------------
    //  Rendering a report
    // ---------------------------------------------------------------------------------------------

    void RenderReport(WrappedReport report)
    {
        if (ContentPanel == null)
            return;

        ContentPanel.Children.Clear();

        ContentPanel.Children.Add(Header(report.PeriodLabel,
            $"{report.DisplayName}".Trim().Length > 0
                ? $"{report.DisplayName} - {report.PeriodStart.LocalDateTime:yyyy-MM-dd} to {report.PeriodEnd.LocalDateTime:yyyy-MM-dd}"
                : $"{report.PeriodStart.LocalDateTime:yyyy-MM-dd} to {report.PeriodEnd.LocalDateTime:yyyy-MM-dd}"));

        if (report.Highlights.Count > 0)
        {
            var highlights = new StackPanel { Spacing = 4 };
            foreach (string highlight in report.Highlights)
                highlights.Children.Add(new TextBlock { Text = "• " + highlight, TextWrapping = TextWrapping.Wrap });
            ContentPanel.Children.Add(Section("The short version", highlights));
        }

        // Scope: what was included, so a partial run is never mistaken for the whole story.
        var scope = new StackPanel { Spacing = 3 };
        scope.Children.Add(Info($"History: {report.HistoryEntriesInPeriod} of {report.HistoryEntriesTotal} recorded events fall inside this period."));
        scope.Children.Add(Info($"Library: {report.SongsInDatabase} songs, {report.SongsWithFiles} with a file, {report.SongsAnalysed} measured from audio" +
            (report.SongsFromCache > 0 ? $" (of which {report.SongsFromCache} came from the cache)" : "") +
            (report.SongsUnreadable > 0 ? $", {report.SongsUnreadable} unreadable" : "") + "."));
        if (report.AudioAnalysisSkipped)
            scope.Children.Add(Info("The audio analysis was switched off for this run; the sound sections are missing."));
        foreach (string note in report.Notes)
            scope.Children.Add(Info(note));
        ContentPanel.Children.Add(Section("What went into this", scope));

        ContentPanel.Children.Add(Section("The numbers", BuildHeadline(report.Headline)));
        ContentPanel.Children.Add(Section("Top artists", BuildArtists(report.TopArtists)));
        ContentPanel.Children.Add(Section("Most played songs", BuildSongs(report.TopSongs, showReason: true)));
        ContentPanel.Children.Add(Section("Obsessions (plays per day of listening)", BuildSongs(report.Obsessions, showReason: true)));
        ContentPanel.Children.Add(Section("One-hit wonders (a burst, then never again)", BuildSongs(report.OneHitWonders, showReason: true)));
        ContentPanel.Children.Add(Section("Hall of fame", BuildSongs(report.HallOfFame, showReason: true)));
        ContentPanel.Children.Add(Section("Hall of shame", BuildSongs(report.HallOfShame, showReason: true)));
        ContentPanel.Children.Add(Section("Most divisive", BuildSongs(report.MostDivisive, showReason: true)));
        ContentPanel.Children.Add(Section("Faded out", BuildSongs(report.FastestFaders, showReason: true)));
        ContentPanel.Children.Add(Section("Rediscovered", BuildSongs(report.Rediscovered, showReason: true)));
        ContentPanel.Children.Add(Section("Newly embraced", BuildSongs(report.NewlyEmbraced, showReason: true)));
        ContentPanel.Children.Add(Section("Positively rated the longest", BuildSongs(report.LongestVoted, showReason: true)));
        ContentPanel.Children.Add(Section("When you listen", BuildRhythm(report.Rhythm)));
        ContentPanel.Children.Add(Section("Sittings", BuildSessions(report.Sessions)));

        if (report.Months.Count > 0)
            ContentPanel.Children.Add(Section("Month by month", BuildMonths(report.Months)));

        if (report.LoyalSongs.Count > 0)
            ContentPanel.Children.Add(Section("Songs that never left", BuildLoyalty(report.LoyalSongs)));

        if (report.Phases.Count > 0)
            ContentPanel.Children.Add(Section("Phases (stretches with their own character)", BuildPhases(report.Phases)));

        if (report.AudioProfile.AnalysedSongs > 0)
        {
            ContentPanel.Children.Add(Section("How your music sounds", BuildAudioProfile(report.AudioProfile)));
            ContentPanel.Children.Add(Section("Your sound worlds", BuildClusters(report.SoundClusters)));
        }

        if (report.SignatureSong != null)
            ContentPanel.Children.Add(Section("The song that sounds most like your period", BuildSongs([report.SignatureSong], showReason: true)));

        if (report.OutlierSong != null)
            ContentPanel.Children.Add(Section("The odd one out", BuildSongs([report.OutlierSong], showReason: true)));

        if (report.ReleaseYears.Count > 0)
            ContentPanel.Children.Add(Section("Release years (from the online lookup)", BuildReleaseYears(report.ReleaseYears)));

        if (report.Keys.Count > 0)
            ContentPanel.Children.Add(Section("Keys", BuildKeys(report.Keys)));
    }

    // ---- section builders ----

    Control BuildHeadline(WrappedHeadline headline)
    {
        var grid = NewGrid(2);
        int row = 0;
        AddRow(grid, row++, "Plays", headline.Plays.ToString("N0"));
        AddRow(grid, row++, "Upvotes / downvotes", $"{headline.Upvotes:N0} / {headline.Downvotes:N0}   ({headline.AverageVoteRatio * 100:0}% up)");
        AddRow(grid, row++, "Different songs", headline.DistinctSongs.ToString("N0"));
        AddRow(grid, row++, "Different artists", headline.DistinctArtists.ToString("N0"));
        AddRow(grid, row++, "Days with music", $"{headline.ActiveDays:N0} of {headline.DaysInPeriod:N0} in the period");
        AddRow(grid, row++, "Plays per day with music", headline.PlaysPerActiveDay.ToString("0.0"));
        AddRow(grid, row++, "Longest daily streak", headline.LongestDailyStreak.ToString("N0"));
        AddRow(grid, row++, "Busiest day", $"{headline.BusiestDay} ({headline.BusiestDayPlays:N0} plays)");
        if (headline.TotalListeningDays > 0)
            AddRow(grid, row, "Estimated playing time", $"{headline.TotalListeningDays:0.0} days of audio");
        return grid;
    }

    Control BuildArtists(List<WrappedArtist> artists)
    {
        if (artists.Count == 0)
            return Info("No artist could be determined for the songs of this period (the file names carry no artist).");

        var grid = NewGrid(5);
        AddHeader(grid, "Artist", "Plays", "Songs", "Up / down", "Unplayed songs of theirs");
        int row = 1;
        foreach (var artist in artists)
        {
            AddCells(grid, row++, artist.Name, artist.Plays.ToString("N0"), artist.DistinctSongs.ToString("N0"),
                $"{artist.Upvotes:N0} / {artist.Downvotes:N0}", artist.UnplayedOwnedSongs.ToString("N0"));
        }
        return grid;
    }

    Control BuildSongs(List<WrappedSong> songs, bool showReason)
    {
        if (songs.Count == 0)
            return Info("Nothing qualified for this list in this period.");

        var grid = NewGrid(showReason ? 6 : 5);
        AddHeader(grid, "Song", "Artist", "Plays", "Up / down", "Score", showReason ? "Why" : "");
        int row = 1;
        foreach (var song in songs)
        {
            string score = song.Streak != 0 ? $"{song.Score:0.0} (streak {song.Streak})" : $"{song.Score:0.0}";
            if (showReason)
                AddCells(grid, row++, song.Name, song.Artist, song.Plays.ToString("N0"),
                    $"{song.Upvotes:N0} / {song.Downvotes:N0}", score, song.Reason);
            else
                AddCells(grid, row++, song.Name, song.Artist, song.Plays.ToString("N0"),
                    $"{song.Upvotes:N0} / {song.Downvotes:N0}", score, "");
        }
        return grid;
    }

    Control BuildRhythm(WrappedListeningRhythm rhythm)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Info($"Peak hour: {rhythm.PeakHour:00}:00 ({rhythm.PeakHourSharePercent:0}% of all plays)   •   " +
                                $"Night (22-04): {rhythm.NightOwlPercent:0}%   •   Morning (06-10): {rhythm.MorningPercent:0}%   •   " +
                                $"Weekend: {rhythm.WeekendPercent:0}%"));

        panel.Children.Add(new TextBlock { Text = "By hour of day", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(BuildBarChart(
            Enumerable.Range(0, 24).Select(hour => ($"{hour:00}", rhythm.ByHour[hour])).ToList(),
            $"the busiest hour is {rhythm.PeakHour:00}:00"));

        panel.Children.Add(new TextBlock { Text = "By weekday", FontWeight = FontWeight.SemiBold });
        string[] weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
        panel.Children.Add(BuildBarChart(
            Enumerable.Range(0, 7).Select(day => (weekdays[day], rhythm.ByWeekday[day])).ToList(),
            $"the busiest day is {weekdays[rhythm.PeakWeekday]}"));

        return panel;
    }

    Control BuildBarChart(List<(string Label, int Value)> bars, string caption)
    {
        int max = Math.Max(1, bars.Count == 0 ? 1 : bars.Max(bar => bar.Value));
        var panel = new StackPanel { Spacing = 2 };

        foreach (var (label, value) in bars)
        {
            if (value == 0)
                continue;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,70") };
            var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(labelBlock, 0);
            row.Children.Add(labelBlock);

            // A proportional bar built from two star-sized columns: no chart control, no binding and no
            // measurement pass, and it still scales with the window because the outer column is flexible.
            var bar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions
                {
                    new ColumnDefinition(new GridLength(Math.Max(0.5, value), GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(Math.Max(0.5, max - value), GridUnitType.Star)),
                },
                Height = 14,
            };
            bar.Children.Add(new Border
            {
                Background = ThemeColors.PrimaryBrush,
                CornerRadius = new CornerRadius(3),
            });
            var remainder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#22FFFFFF")),
                CornerRadius = new CornerRadius(3),
            };
            Grid.SetColumn(remainder, 1);
            bar.Children.Add(remainder);
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var valueBlock = new TextBlock
            {
                Text = value.ToString("N0"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(valueBlock, 2);
            row.Children.Add(valueBlock);

            panel.Children.Add(row);
        }

        if (caption.Length > 0)
            panel.Children.Add(Info(caption));
        return panel;
    }

    Control BuildSessions(WrappedSessionStats sessions)
    {
        if (sessions.SessionCount == 0)
            return Info("No sittings found in this period.");

        var grid = NewGrid(2);
        int row = 0;
        AddRow(grid, row++, "Listening sittings", $"{sessions.SessionCount:N0} (a new one starts after a {sessions.GapMinutes:0} minute break)");
        AddRow(grid, row++, "Songs per sitting", $"{sessions.AverageSongsPerSession:0.0} on average, {sessions.MedianSongsPerSession:0} typical");
        AddRow(grid, row++, "Length of a sitting", $"{sessions.AverageSessionMinutes:0} minutes on average");
        AddRow(grid, row++, "Longest sitting", $"{sessions.LongestSessionSongs:N0} songs in {sessions.LongestSessionMinutes:0} minutes" +
            (sessions.LongestSessionArtist.Length > 0 ? $" ({sessions.LongestSessionArtist})" : ""));
        AddRow(grid, row++, "Marathon share", $"{sessions.MarathonSharePercent:0}% of the plays happened in sittings of 20+ songs");
        if (sessions.AverageDaysBetweenSessions > 0)
            AddRow(grid, row, "Between sittings", $"{sessions.AverageDaysBetweenSessions:0.0} days on average");
        return grid;
    }

    Control BuildMonths(List<WrappedMonth> months)
    {
        var grid = NewGrid(6);
        AddHeader(grid, "Month", "Plays", "Songs", "Days", "Top artist", "Top song");
        int row = 1;
        foreach (var month in months)
            AddCells(grid, row++, month.Month, month.Plays.ToString("N0"), month.DistinctSongs.ToString("N0"),
                month.ActiveDays.ToString("N0"), month.TopArtist, month.TopSong);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BuildBarChart(months.Select(month => (month.Month, month.Plays)).ToList(), ""));
        panel.Children.Add(grid);
        return panel;
    }

    Control BuildLoyalty(List<WrappedLoyalSong> loyal)
    {
        var grid = NewGrid(4);
        AddHeader(grid, "Song", "Artist", "Years played", "Plays per year");
        int row = 1;
        foreach (var entry in loyal)
            AddCells(grid, row++, entry.Song.Name, entry.Song.Artist,
                $"{entry.YearsPlayed} ({string.Join(", ", entry.Years)})",
                string.Join(" / ", entry.PlaysPerYear));
        return grid;
    }

    Control BuildPhases(List<WrappedPhase> phases)
    {
        var grid = NewGrid(6);
        AddHeader(grid, "Period", "Plays", "Top artist", "Top song", "Character", "Sound");
        int row = 1;
        foreach (var phase in phases)
        {
            string character = $"{phase.DistinctSongs} songs, {phase.ArtistConcentrationPercent:0}% inside its top 5 artists";
            string sound = phase.AverageBpm > 0
                ? $"{phase.AverageBpm:0} BPM, loudness {phase.AverageLoudness:0.00}, brightness {phase.AverageBrightnessHz:0} Hz"
                : "";
            AddCells(grid, row++,
                $"{phase.Start.LocalDateTime:yyyy-MM-dd} to {phase.End.LocalDateTime:yyyy-MM-dd}",
                phase.Plays.ToString("N0"), phase.TopArtist, phase.TopSong, character, sound);
        }
        return grid;
    }

    Control BuildAudioProfile(WrappedAudioProfile profile)
    {
        var panel = new StackPanel { Spacing = 10 };
        var grid = NewGrid(2);
        int row = 0;
        AddRow(grid, row++, "Songs measured", profile.AnalysedSongs.ToString("N0"));
        AddRow(grid, row++, "Tempo", $"{profile.AverageBpm:0} BPM on average, {profile.MedianBpm:0} BPM typical");
        AddRow(grid, row++, "Loudness", $"mean {profile.AverageLoudness:0.000}, dynamic range {profile.AverageDynamicRange:0.000}, crest {profile.AverageCrestFactorDb:0.0} dB");
        AddRow(grid, row++, "Loud share of a song", $"{profile.AverageLoudFractionPercent:0}% on average (a high value means wall-to-wall loud)");
        AddRow(grid, row++, "Brightness", $"{profile.AverageBrightnessHz:0} Hz average spectral centre");
        AddRow(grid, row++, "Minor keys", $"{profile.MinorSharePercent:0}% of the library" + (profile.MostCommonKey.Length > 0 ? $", most common key {profile.MostCommonKey}" : ""));
        if (profile.PlayedAverageBpm > 0)
            AddRow(grid, row++, "What you played", $"{profile.PlayedAverageBpm:0} BPM, loudness {profile.PlayedAverageLoudness:0.000}, brightness {profile.PlayedAverageBrightnessHz:0} Hz");
        panel.Children.Add(grid);

        if (profile.BpmHistogram.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Tempo distribution (songs in the library / plays in this period)", FontWeight = FontWeight.SemiBold });
            var grid2 = NewGrid(4);
            AddHeader(grid2, "Tempo", "Songs", "Plays", "Share of the library");
            int row2 = 1;
            int total = Math.Max(1, profile.AnalysedSongs);
            foreach (var bucket in profile.BpmHistogram)
                AddCells(grid2, row2++, $"{bucket.FromBpm}-{bucket.ToBpm} BPM", bucket.SongCount.ToString("N0"),
                    bucket.PlayCount.ToString("N0"), $"{100.0 * bucket.SongCount / total:0.0}%");
            panel.Children.Add(grid2);
        }

        return panel;
    }

    Control BuildClusters(List<WrappedSoundCluster> clusters)
    {
        if (clusters.Count == 0)
            return Info("Not enough measured songs to group them by sound.");

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Info("These groups come from the measured sound of your library (tempo, timbre, key, loudness) - " +
                                "no online genre database is involved, so the labels describe what the music measurably is."));

        foreach (var cluster in clusters)
        {
            var card = new StackPanel { Spacing = 4 };
            card.Children.Add(new TextBlock
            {
                Text = $"{cluster.Label}",
                FontWeight = FontWeight.Bold,
                FontSize = 15,
            });
            card.Children.Add(Info($"{cluster.SongCount:N0} songs ({cluster.LibrarySharePercent:0}% of the measured library), " +
                                   $"{cluster.Plays:N0} plays in this period ({cluster.PlaySharePercent:0}% of them), " +
                                   $"{cluster.PlaysPerSong:0.0} plays per song, {cluster.UntouchedPercent:0}% never played in the period."));
            card.Children.Add(Info($"Sound: {cluster.AverageBpm:0} BPM, loudness {cluster.AverageLoudness:0.000}, brightness {cluster.AverageBrightnessHz:0} Hz, " +
                                   $"net likes {cluster.NetLikes:N0}."));
            if (cluster.Characteristics.Count > 0)
                card.Children.Add(Info("Stands out as: " + string.Join("; ", cluster.Characteristics)));
            if (cluster.TopArtists.Count > 0)
                card.Children.Add(Info("Artists: " + string.Join(", ", cluster.TopArtists)));
            if (cluster.TopSongs.Count > 0)
                card.Children.Add(Info("Most played in it: " + string.Join(", ", cluster.TopSongs
                    .Where(song => song.Plays > 0)
                    .Take(5)
                    .Select(song => $"{song.Name} ({song.Plays})"))));

            var border = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#12FFFFFF")),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Child = card,
            };
            panel.Children.Add(border);
        }

        return panel;
    }

    Control BuildReleaseYears(List<WrappedReleaseYear> years)
    {
        var grid = NewGrid(4);
        AddHeader(grid, "Release year", "Songs", "Plays", "Artists");
        int row = 1;
        foreach (var year in years)
            AddCells(grid, row++, year.Year.ToString(), year.SongCount.ToString("N0"),
                year.Plays.ToString("N0"), year.DistinctArtists.ToString("N0"));
        return grid;
    }

    Control BuildKeys(List<WrappedKeyCount> keys)
    {
        var grid = NewGrid(4);
        AddHeader(grid, "Key", "Songs", "Plays", "Net likes");
        int row = 1;
        foreach (var key in keys.Take(15))
            AddCells(grid, row++, key.Key, key.SongCount.ToString("N0"), key.Plays.ToString("N0"), key.NetLikes.ToString("N0"));
        return grid;
    }

    // ---- small control helpers ----

    static Grid NewGrid(int columns)
    {
        var definitions = new ColumnDefinitions();
        // First column flexible, the rest sized to content, so long song names wrap into the space that is
        // left instead of pushing the numbers off screen.
        definitions.Add(new ColumnDefinition(GridLength.Star));
        for (int i = 1; i < columns; i++)
            definitions.Add(new ColumnDefinition(GridLength.Auto));
        return new Grid { ColumnDefinitions = definitions, RowDefinitions = new RowDefinitions(), Margin = new Thickness(0, 2, 0, 2) };
    }

    static void AddHeader(Grid grid, params string[] headers)
    {
        for (int column = 0; column < headers.Length; column++)
        {
            var block = new TextBlock
            {
                Text = headers[column],
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 12, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(block, column);
            Grid.SetRow(block, 0);
            grid.Children.Add(block);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
    }

    static void AddCells(Grid grid, int row, params string[] cells)
    {
        for (int column = 0; column < cells.Length; column++)
        {
            if (cells[column].Length == 0)
                continue;

            var block = new TextBlock
            {
                Text = cells[column],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(block, column);
            Grid.SetRow(block, row);
            grid.Children.Add(block);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
    }

    static void AddRow(Grid grid, int row, string label, string value)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.Parse("#99FFFFFF")),
            Margin = new Thickness(0, 0, 16, 2),
        };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(labelBlock, row);
        grid.Children.Add(labelBlock);

        var valueBlock = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        Grid.SetColumn(valueBlock, 1);
        Grid.SetRow(valueBlock, row);
        grid.Children.Add(valueBlock);

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
    }

    static Control Info(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.Parse("#CCFFFFFF")),
    };

    static Control Header(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = new SolidColorBrush(Color.Parse("#99FFFFFF")),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    static Control Section(string title, Control content)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeColors.PrimaryBrush,
        });
        panel.Children.Add(content);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0EFFFFFF")),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = panel,
        };
    }

    // ---------------------------------------------------------------------------------------------
    //  Plain text export
    // ---------------------------------------------------------------------------------------------

    /// <summary>A report as text, for the clipboard - the same content as the window, without any styling.</summary>
    public static string RenderReportAsText(WrappedReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"Music player wrapped - {report.PeriodLabel}");
        text.AppendLine($"{report.PeriodStart.LocalDateTime:yyyy-MM-dd} to {report.PeriodEnd.LocalDateTime:yyyy-MM-dd}");
        if (report.DisplayName.Length > 0)
            text.AppendLine(report.DisplayName);
        text.AppendLine($"(computed {report.ComputedAt.LocalDateTime:yyyy-MM-dd HH:mm}, {WrappedService.FormatDuration(TimeSpan.FromSeconds(report.ComputationSeconds))})");
        text.AppendLine();

        foreach (string highlight in report.Highlights)
            text.AppendLine($"* {highlight}");
        if (report.Highlights.Count > 0)
            text.AppendLine();

        text.AppendLine("NUMBERS");
        text.AppendLine($"  plays:                {report.Headline.Plays}");
        text.AppendLine($"  up / down:            {report.Headline.Upvotes} / {report.Headline.Downvotes}");
        text.AppendLine($"  different songs:      {report.Headline.DistinctSongs}");
        text.AppendLine($"  different artists:    {report.Headline.DistinctArtists}");
        text.AppendLine($"  days with music:      {report.Headline.ActiveDays} of {report.Headline.DaysInPeriod}");
        text.AppendLine($"  longest daily streak: {report.Headline.LongestDailyStreak}");
        text.AppendLine();

        AppendSongs(text, "MOST PLAYED", report.TopSongs);
        AppendSongs(text, "OBSESSIONS", report.Obsessions);
        AppendSongs(text, "HALL OF FAME", report.HallOfFame);
        AppendSongs(text, "HALL OF SHAME", report.HallOfShame);
        AppendSongs(text, "MOST DIVISIVE", report.MostDivisive);
        AppendSongs(text, "FADED OUT", report.FastestFaders);
        AppendSongs(text, "REDISCOVERED", report.Rediscovered);
        AppendSongs(text, "NEWLY EMBRACED", report.NewlyEmbraced);

        if (report.TopArtists.Count > 0)
        {
            text.AppendLine("TOP ARTISTS");
            foreach (var artist in report.TopArtists)
                text.AppendLine($"  {artist.Plays,6} plays  {artist.Name} ({artist.DistinctSongs} songs)");
            text.AppendLine();
        }

        if (report.SoundClusters.Count > 0)
        {
            text.AppendLine("SOUND WORLDS");
            foreach (var cluster in report.SoundClusters)
            {
                text.AppendLine($"  {cluster.Label}: {cluster.SongCount} songs, {cluster.Plays} plays, {cluster.PlaysPerSong:0.0} plays per song");
                foreach (string characteristic in cluster.Characteristics)
                    text.AppendLine($"      - {characteristic}");
            }
            text.AppendLine();
        }

        foreach (string note in report.Notes)
            text.AppendLine($"note: {note}");
        return text.ToString();
    }

    static void AppendSongs(StringBuilder text, string title, List<WrappedSong> songs)
    {
        if (songs.Count == 0)
            return;

        text.AppendLine(title);
        foreach (var song in songs)
        {
            string artist = song.Artist.Length > 0 ? $"{song.Artist} - " : "";
            text.AppendLine($"  {song.Plays,6} plays  {artist}{song.Name}");
            if (song.Reason.Length > 0)
                text.AppendLine($"                 ({song.Reason})");
        }
        text.AppendLine();
    }
}
