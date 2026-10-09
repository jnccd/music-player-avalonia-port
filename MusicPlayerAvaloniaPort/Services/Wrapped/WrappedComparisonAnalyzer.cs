using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// Compares one period of listening with the one before it ("you listened 40 hours more than last year").
/// <para>
/// Kept apart from <see cref="WrappedHistoryAnalyzer"/> because it works on two periods at once: the
/// analyzer answers "what happened", this answers "what changed". Every number here is derived from the
/// same raw evidence as the rest of the report (the history rows plus each song's audio features), never
/// from the already-aggregated sections of the previous report - so a comparison is still correct when the
/// previous year's report was never computed or was deleted.
/// </para>
/// </summary>
public static class WrappedComparisonAnalyzer
{
    /// <summary>
    /// Builds the comparison between two periods. <paramref name="currentEvents"/> are the caller's already
    /// filtered events for the later period; <paramref name="previousEvents"/> the earlier one's;
    /// <paramref name="earlierThanPreviousEvents"/> everything before the earlier period, which is what
    /// makes "you came back to this artist" distinguishable from "this artist is new to you".
    /// </summary>
    public static WrappedYearComparison Build(
        int previousYear,
        int currentYear,
        IReadOnlyList<WrappedSongInput> currentSongs,
        IReadOnlyList<(WrappedSongInput Song, WrappedEvent Event)> currentEvents,
        IReadOnlyList<WrappedSongInput> previousSongs,
        IReadOnlyList<(WrappedSongInput Song, WrappedEvent Event)> previousEvents,
        IReadOnlyList<(WrappedSongInput Song, WrappedEvent Event)> earlierThanPreviousEvents,
        Func<WrappedSongInput, double>? secondsPlayedPerSong)
    {
        var comparison = new WrappedYearComparison
        {
            PreviousYear = previousYear,
            CurrentYear = currentYear,
        };

        var current = Summarize(currentSongs, currentEvents, secondsPlayedPerSong);
        var previous = Summarize(previousSongs, previousEvents, secondsPlayedPerSong);
        var longBefore = ArtistKeys(earlierThanPreviousEvents);

        comparison.Metrics = BuildMetrics(current, previous);
        BuildArtistMovement(comparison, current, previous, longBefore);
        comparison.Highlights = BuildHighlights(comparison, current, previous);
        return comparison;
    }

    /// <summary>Everything the comparison needs about one period.</summary>
    sealed class PeriodSummary
    {
        public int Plays;
        public int Upvotes;
        public int Downvotes;
        public int DistinctSongs;
        public int DistinctArtists;
        public int ActiveDays;
        public int LongestStreak;
        public int SessionCount;
        public double ListeningHours;
        /// <summary>True when every played song's duration was known, so the hours are complete.</summary>
        public bool ListeningHoursComplete;
        /// <summary>Plays per artist group (keyed by the normalized artist name).</summary>
        public Dictionary<string, ArtistPlays> Artists = new(StringComparer.Ordinal);
        // The audio-based comparisons (average tempo/loudness/brightness) are added by WrappedService,
        // which is where the measured features live; this summary deliberately covers only the history.
    }

    sealed class ArtistPlays
    {
        public string Name = "";
        public int Plays;
    }

    static PeriodSummary Summarize(
        IReadOnlyList<WrappedSongInput> songs,
        IReadOnlyList<(WrappedSongInput Song, WrappedEvent Event)> events,
        Func<WrappedSongInput, double>? secondsPlayedPerSong)
    {
        var summary = new PeriodSummary { Plays = events.Count };
        if (events.Count == 0)
            return summary;

        summary.Upvotes = events.Count(entry => entry.Event.IsUpvote);
        summary.Downvotes = events.Count(entry => entry.Event.IsDownvote);
        summary.DistinctSongs = events.Select(entry => entry.Song.SongId).Distinct().Count();

        var activeDays = events
            .Select(entry => DateOnly.FromDateTime(entry.Event.Date.LocalDateTime))
            .Distinct()
            .OrderBy(day => day)
            .ToList();
        summary.ActiveDays = activeDays.Count;
        summary.LongestStreak = LongestStreak(activeDays);

        // Sittings, using the same gap the report itself uses, so "more sittings" means the same thing in
        // both places.
        var ordered = events.OrderBy(entry => entry.Event.Date).ToList();
        int sessions = 1;
        for (int i = 1; i < ordered.Count; i++)
            if ((ordered[i].Event.Date - ordered[i - 1].Event.Date).TotalMinutes > WrappedHistoryAnalyzer.SessionGapMinutes)
                sessions++;
        summary.SessionCount = sessions;

        // Listening time, but only from songs whose duration was measured, and only claimed as complete when
        // every played song had one - otherwise the hour count would silently understate the period.
        if (secondsPlayedPerSong != null)
        {
            double seconds = 0.0;
            int songsWithDuration = 0;
            foreach (var group in events.GroupBy(entry => entry.Song.SongId))
            {
                double perSong = secondsPlayedPerSong(group.First().Song);
                if (perSong <= 0)
                    continue;
                songsWithDuration++;
                seconds += perSong * group.Count();
            }
            summary.ListeningHours = seconds / 3600.0;
            summary.ListeningHoursComplete = songsWithDuration == events.Select(entry => entry.Song.SongId).Distinct().Count();
        }

        foreach (var group in events
            .Select(entry => (Key: ArtistNameParser.ArtistKey(
                    ArtistNameParser.Parse(entry.Song.StoredArtist, entry.Song.Name).Artist),
                entry.Song))
            .Where(entry => entry.Key.Length > 0)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal))
        {
            summary.Artists[group.Key] = new ArtistPlays
            {
                Name = ArtistNameParser.ChooseDisplayName(group.Select(entry => ArtistNameParser.Parse(entry.Song.StoredArtist, entry.Song.Name).Artist)),
                Plays = group.Count(),
            };
        }
        summary.DistinctArtists = summary.Artists.Count;

        return summary;
    }

    static List<WrappedComparisonMetric> BuildMetrics(PeriodSummary current, PeriodSummary previous)
    {
        var metrics = new List<WrappedComparisonMetric>();

        void Add(string label, double previousValue, double currentValue, string unit, string format, int decimals,
            bool percentagePoints = false, bool relativeChange = false, bool riseIsPositive = true)
        {
            metrics.Add(new WrappedComparisonMetric
            {
                Label = label,
                Previous = previousValue,
                Current = currentValue,
                Delta = currentValue - previousValue,
                PreviousText = previousValue.ToString(format),
                CurrentText = currentValue.ToString(format),
                Unit = unit,
                Decimals = decimals,
                RelativeChange = relativeChange,
                IsPercentagePoints = percentagePoints,
                RiseIsPositive = riseIsPositive,
            });
        }

        // Counts are formatted with a plain group separator ('#'): the invariant thousands separator is a
        // comma, which would collide with the decimal comma of the cultures this client is used in and make
        // "4.356" ambiguous between "four thousand" and "four and a bit".
        const string CountFormat = "#,0";

        // Hours and plays lead: they are the "how much did I listen" answer the feature exists for.
        if (current.ListeningHours > 0 || previous.ListeningHours > 0)
            Add("Hours listened", previous.ListeningHours, current.ListeningHours, "hours", "0.0", 1);

        Add("Plays", previous.Plays, current.Plays, "plays", CountFormat, 0);
        Add("Different songs", previous.DistinctSongs, current.DistinctSongs, "songs", CountFormat, 0);
        Add("Different artists", previous.DistinctArtists, current.DistinctArtists, "artists", CountFormat, 0);
        Add("Days with music", previous.ActiveDays, current.ActiveDays, "days", CountFormat, 0);
        Add("Listening sittings", previous.SessionCount, current.SessionCount, "sittings", CountFormat, 0);
        Add("Longest daily streak", previous.LongestStreak, current.LongestStreak, "days", CountFormat, 0);

        double previousRatio = previous.Plays > 0 ? 100.0 * previous.Upvotes / previous.Plays : 0.0;
        double currentRatio = current.Plays > 0 ? 100.0 * current.Upvotes / current.Plays : 0.0;
        Add("Upvoted share of plays", previousRatio, currentRatio, "%", "0.#", 1, percentagePoints: true);

        double previousPerActiveDay = previous.ActiveDays > 0 ? previous.Plays / (double)previous.ActiveDays : 0.0;
        double currentPerActiveDay = current.ActiveDays > 0 ? current.Plays / (double)current.ActiveDays : 0.0;
        Add("Plays per day with music", previousPerActiveDay, currentPerActiveDay, "plays", "0.0", 1);

        return metrics;
    }

    /// <summary>
    /// Which artists moved. Only artists played in both periods can have "grown" or "fallen" - an artist new
    /// this period is reported under <see cref="WrappedYearComparison.NewArtists"/> instead, because "up
    /// from zero" and "up from 40 plays" are different stories.
    /// </summary>
    static void BuildArtistMovement(
        WrappedYearComparison comparison,
        PeriodSummary current,
        PeriodSummary previous,
        HashSet<string> longBefore)
    {
        var shifts = new List<WrappedArtistShift>();
        foreach (var pair in current.Artists)
        {
            if (!previous.Artists.TryGetValue(pair.Key, out var before))
                continue;

            shifts.Add(new WrappedArtistShift
            {
                Name = pair.Value.Name,
                PreviousPlays = before.Plays,
                CurrentPlays = pair.Value.Plays,
                Delta = pair.Value.Plays - before.Plays,
            });
        }

        comparison.ArtistGains = shifts
            .Where(shift => shift.Delta > 0)
            .OrderByDescending(shift => shift.Delta)
            .ThenByDescending(shift => shift.CurrentPlays)
            .Take(5)
            .ToList();

        comparison.ArtistLosses = shifts
            .Where(shift => shift.Delta < 0)
            .OrderBy(shift => shift.Delta)
            .ThenByDescending(shift => shift.PreviousPlays)
            .Take(5)
            .ToList();

        comparison.NewArtists = current.Artists
            .Where(pair => !previous.Artists.ContainsKey(pair.Key))
            .OrderByDescending(pair => pair.Value.Plays)
            .Take(8)
            .Select(pair => pair.Value.Name)
            .ToList();

        // "Came back" needs a gap: played this period, not in the previous one, but at some point before it.
        // Without the third set every new artist would look like a returning one.
        comparison.ReturningArtists = current.Artists
            .Where(pair => !previous.Artists.ContainsKey(pair.Key) && longBefore.Contains(pair.Key))
            .OrderByDescending(pair => pair.Value.Plays)
            .Take(8)
            .Select(pair => pair.Value.Name)
            .ToList();
    }

    /// <summary>The set of artist keys played in a list of events.</summary>
    static HashSet<string> ArtistKeys(IReadOnlyList<(WrappedSongInput Song, WrappedEvent Event)> events) =>
        events
            .Select(entry => ArtistNameParser.ArtistKey(
                ArtistNameParser.Parse(entry.Song.StoredArtist, entry.Song.Name).Artist))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    static List<string> BuildHighlights(WrappedYearComparison comparison, PeriodSummary current, PeriodSummary previous)
    {
        var highlights = new List<string>();

        var hours = comparison.Metrics.FirstOrDefault(metric => metric.Label == "Hours listened");
        if (hours != null && Math.Abs(hours.Delta) >= 1.0)
        {
            string direction = hours.Delta > 0 ? "more" : "less";
            highlights.Add($"You listened {Math.Abs(hours.Delta):0} hours {direction} music than in {comparison.PreviousYear} " +
                           $"({hours.CurrentText} vs {hours.PreviousText} hours).");
        }

        var plays = comparison.Metrics.FirstOrDefault(metric => metric.Label == "Plays");
        if (plays != null && previous.Plays > 0)
        {
            double change = 100.0 * plays.Delta / previous.Plays;
            if (Math.Abs(change) >= 5)
                highlights.Add($"That is {Math.Abs(change):0}% {(change > 0 ? "more" : "fewer")} plays than the year before.");
        }

        if (current.DistinctArtists != previous.DistinctArtists && previous.DistinctArtists > 0)
        {
            int change = current.DistinctArtists - previous.DistinctArtists;
            highlights.Add(change > 0
                ? $"You widened out: {change} more different artists than in {comparison.PreviousYear}."
                : $"You narrowed down: {Math.Abs(change)} fewer different artists than in {comparison.PreviousYear}.");
        }

        if (comparison.NewArtists.Count > 0)
            highlights.Add($"{comparison.NewArtists.Count} artists are new for you this year, led by {comparison.NewArtists[0]}.");

        if (comparison.ReturningArtists.Count > 0)
            highlights.Add($"You came back to {comparison.ReturningArtists.Count} artist(s) you had not played in {comparison.PreviousYear}, " +
                           $"starting with {comparison.ReturningArtists[0]}.");

        if (comparison.ArtistGains.Count > 0 && comparison.ArtistGains[0].Delta >= 5)
            highlights.Add($"{comparison.ArtistGains[0].Name} grew the most: +{comparison.ArtistGains[0].Delta} plays.");

        if (comparison.ArtistLosses.Count > 0 && comparison.ArtistLosses[0].Delta <= -5)
            highlights.Add($"{comparison.ArtistLosses[0].Name} fell the furthest: {comparison.ArtistLosses[0].Delta} plays.");

        var activeDays = comparison.Metrics.FirstOrDefault(metric => metric.Label == "Days with music");
        if (activeDays != null && Math.Abs(activeDays.Delta) >= 5)
            highlights.Add(activeDays.Delta > 0
                ? $"You had {activeDays.Delta:0} more days with music than in {comparison.PreviousYear}."
                : $"{Math.Abs(activeDays.Delta):0} fewer days with music than in {comparison.PreviousYear}.");

        return highlights;
    }

    static int LongestStreak(List<DateOnly> orderedDays)
    {
        if (orderedDays.Count == 0)
            return 0;

        int longest = 1;
        int running = 1;
        for (int i = 1; i < orderedDays.Count; i++)
        {
            if (orderedDays[i].DayNumber == orderedDays[i - 1].DayNumber + 1)
                running++;
            else
                running = 1;
            longest = Math.Max(longest, running);
        }
        return longest;
    }
}
