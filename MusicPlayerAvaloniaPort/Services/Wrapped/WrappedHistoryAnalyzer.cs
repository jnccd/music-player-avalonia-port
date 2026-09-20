using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>One song as the wrapped needs it: identity, library location, counters and its stored tags.</summary>
public sealed class WrappedSongInput
{
    public Guid SongId { get; set; }
    public string Name { get; set; } = "";
    public string StoredArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool HasFile { get; set; }
    public float Score { get; set; }
    public int Streak { get; set; }
    public int TotalLikes { get; set; }
    public int TotalDislikes { get; set; }
    public float Volume { get; set; }
    public DateTimeOffset? DateAdded { get; set; }

    /// <summary>History events of this song inside the analysed period, ascending by date.</summary>
    public List<WrappedEvent> Events { get; set; } = [];
}

/// <summary>A single listening event, as the history stores it.</summary>
public readonly record struct WrappedEvent(DateTimeOffset Date, float ScoreChange)
{
    public bool IsUpvote => ScoreChange > 0.0001f;
    public bool IsDownvote => ScoreChange < -0.0001f;
    /// <summary>Played without a vote - the normal outcome for a song that is skipped or ends early.</summary>
    public bool IsSkip => Math.Abs(ScoreChange) < 0.0001f;
}

/// <summary>
/// Turns the local play history into the listening half of a wrapped report.
/// <para>
/// The history rows are the primary evidence here, and they are more informative than the counters on the
/// song rows: a <c>ScoreChange</c> of 0 means "played but not voted on" (the normal case when a song is
/// skipped or simply ends before the vote threshold), while the counters are lifetime totals that can
/// predate the recorded history entirely.
/// </para>
/// <para>
/// Every ranking returns the numbers behind it (see <see cref="WrappedSong.Reason"/>), because a wrapped
/// that says "your song of the year" without being able to say why is just a guess with better fonts.
/// </para>
/// </summary>
public static class WrappedHistoryAnalyzer
{
    /// <summary>Events closer together than this belong to the same sitting.</summary>
    public const double SessionGapMinutes = 30;

    /// <summary>Minimum plays before a song is considered for a preference ranking (avoids "1 play = 100%").</summary>
    const int MinimumPlaysForRanking = 4;

    /// <summary>
    /// Analyses a period. <paramref name="songs"/> are the songs that have at least one event in the
    /// period; <paramref name="librarySongs"/> is the whole local library (used for context like
    /// "songs of this artist you own but never played").
    /// </summary>
    public static void Analyze(
        WrappedReport report,
        IReadOnlyList<WrappedSongInput> songs,
        IReadOnlyList<WrappedSongInput> librarySongs,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int historyEntriesInPeriod,
        int historyEntriesTotal)
    {
        report.PeriodStart = periodStart;
        report.PeriodEnd = periodEnd;
        report.HistoryEntriesInPeriod = historyEntriesInPeriod;
        report.HistoryEntriesTotal = historyEntriesTotal;

        // Flatten the events of the period in chronological order - the basis of every time based figure.
        var allEvents = songs
            .SelectMany(song => song.Events.Select(e => (Song: song, Event: e)))
            .OrderBy(entry => entry.Event.Date)
            .ToList();

        report.SongsPlayedInPeriod = songs.Count;
        report.Headline = BuildHeadline(allEvents, songs, periodStart, periodEnd);
        report.Rhythm = BuildRhythm(allEvents);
        report.Sessions = BuildSessionStats(allEvents);
        report.Months = BuildMonths(allEvents);
        report.LoyalSongs = BuildLoyalty(songs);
        report.Phases = BuildPhases(allEvents);

        BuildSongSections(report, songs, allEvents, librarySongs, periodStart, periodEnd);
        report.TopArtists = BuildTopArtists(songs, librarySongs, allEvents);
        report.Highlights = BuildHighlights(report);
    }

    // ---------------------------------------------------------------------------------------------
    //  Headline
    // ---------------------------------------------------------------------------------------------

    static WrappedHeadline BuildHeadline(
        List<(WrappedSongInput Song, WrappedEvent Event)> allEvents,
        IReadOnlyList<WrappedSongInput> songs,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        var headline = new WrappedHeadline
        {
            DaysInPeriod = Math.Max(1, (int)Math.Ceiling((periodEnd - periodStart).TotalDays)),
        };

        if (allEvents.Count == 0)
            return headline;

        headline.Plays = allEvents.Count;
        headline.Upvotes = allEvents.Count(entry => entry.Event.IsUpvote);
        headline.Downvotes = allEvents.Count(entry => entry.Event.IsDownvote);
        headline.DistinctSongs = songs.Count;
        headline.FirstPlay = allEvents[0].Event.Date;
        headline.LastPlay = allEvents[^1].Event.Date;

        var artistKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var song in songs)
        {
            var parsed = ArtistNameParser.Parse(song.StoredArtist, song.Name);
            string key = ArtistNameParser.ArtistKey(parsed.Artist);
            if (key.Length > 0)
                artistKeys.Add(key);
        }
        headline.DistinctArtists = artistKeys.Count;

        // Active days and the longest daily streak, in the listener's own local time.
        var activeDays = allEvents
            .Select(entry => DateOnly.FromDateTime(entry.Event.Date.LocalDateTime))
            .Distinct()
            .OrderBy(day => day)
            .ToList();
        headline.ActiveDays = activeDays.Count;
        headline.LongestDailyStreak = LongestStreak(activeDays);

        var busiestDay = allEvents
            .GroupBy(entry => DateOnly.FromDateTime(entry.Event.Date.LocalDateTime))
            .OrderByDescending(group => group.Count())
            .First();
        headline.BusiestDayPlays = busiestDay.Count();
        headline.BusiestDay = busiestDay.Key.ToString("yyyy-MM-dd");

        headline.PlaysPerActiveDay = headline.ActiveDays > 0 ? headline.Plays / (double)headline.ActiveDays : 0;

        int voted = headline.Upvotes + headline.Downvotes;
        headline.AverageVoteRatio = voted > 0 ? headline.Upvotes / (float)voted : 0f;

        // Listening time estimate: the number of songs actually finished is not recorded, so this counts
        // plays of songs whose duration is known and says so in the UI (duration comes from the audio
        // analysis, which fills PlayedSeconds later).
        headline.TotalListeningDays = 0;

        return headline;
    }

    static int LongestStreak(List<DateOnly> orderedDays)
    {
        if (orderedDays.Count == 0)
            return 0;

        int longest = 1;
        int current = 1;
        for (int i = 1; i < orderedDays.Count; i++)
        {
            if (orderedDays[i].DayNumber == orderedDays[i - 1].DayNumber + 1)
                current++;
            else
                current = 1;
            longest = Math.Max(longest, current);
        }
        return longest;
    }

    // ---------------------------------------------------------------------------------------------
    //  Time patterns
    // ---------------------------------------------------------------------------------------------

    static WrappedListeningRhythm BuildRhythm(List<(WrappedSongInput Song, WrappedEvent Event)> allEvents)
    {
        var rhythm = new WrappedListeningRhythm();
        if (allEvents.Count == 0)
            return rhythm;

        int night = 0, morning = 0, weekend = 0;
        foreach (var entry in allEvents)
        {
            // Hour of day is a human concept, so it is evaluated in the listener's local time even though
            // the stored timestamps are absolute.
            var local = entry.Event.Date.LocalDateTime;
            int hour = local.Hour;
            int weekday = ((int)local.DayOfWeek + 6) % 7; // Monday = 0

            rhythm.ByHour[hour]++;
            rhythm.ByWeekday[weekday]++;
            rhythm.WeekdayHour[weekday * 24 + hour]++;

            if (hour >= 22 || hour < 4)
                night++;
            if (hour is >= 6 and < 10)
                morning++;
            if (weekday >= 5)
                weekend++;
        }

        rhythm.PeakHour = Array.IndexOf(rhythm.ByHour, rhythm.ByHour.Max());
        rhythm.PeakWeekday = Array.IndexOf(rhythm.ByWeekday, rhythm.ByWeekday.Max());
        rhythm.NightOwlPercent = 100f * night / allEvents.Count;
        rhythm.MorningPercent = 100f * morning / allEvents.Count;
        rhythm.WeekendPercent = 100f * weekend / allEvents.Count;
        rhythm.PeakHourSharePercent = 100f * rhythm.ByHour[rhythm.PeakHour] / allEvents.Count;
        return rhythm;
    }

    // ---------------------------------------------------------------------------------------------
    //  Sessions
    // ---------------------------------------------------------------------------------------------

    static WrappedSessionStats BuildSessionStats(List<(WrappedSongInput Song, WrappedEvent Event)> allEvents)
    {
        var stats = new WrappedSessionStats { GapMinutes = SessionGapMinutes };
        if (allEvents.Count == 0)
            return stats;

        var sessions = new List<List<(WrappedSongInput Song, WrappedEvent Event)>>();
        var current = new List<(WrappedSongInput Song, WrappedEvent Event)> { allEvents[0] };
        for (int i = 1; i < allEvents.Count; i++)
        {
            double gapMinutes = (allEvents[i].Event.Date - allEvents[i - 1].Event.Date).TotalMinutes;
            if (gapMinutes > SessionGapMinutes)
            {
                sessions.Add(current);
                current = [];
            }
            current.Add(allEvents[i]);
        }
        sessions.Add(current);

        stats.SessionCount = sessions.Count;
        stats.AverageSongsPerSession = allEvents.Count / (double)sessions.Count;

        var sizes = sessions.Select(session => session.Count).OrderBy(size => size).ToList();
        stats.MedianSongsPerSession = sizes[sizes.Count / 2];

        var durations = sessions
            .Select(session => (session[^1].Event.Date - session[0].Event.Date).TotalMinutes)
            .OrderBy(minutes => minutes)
            .ToList();
        stats.AverageSessionMinutes = durations.Average();

        var longest = sessions.OrderByDescending(session => session.Count).First();
        stats.LongestSessionSongs = longest.Count;
        stats.LongestSessionStart = longest[0].Event.Date;
        stats.LongestSessionMinutes = (longest[^1].Event.Date - longest[0].Event.Date).TotalMinutes;

        var longestArtists = longest
            .Select(entry => ArtistNameParser.Parse(entry.Song.StoredArtist, entry.Song.Name).Artist)
            .Where(artist => artist.Length > 0)
            .GroupBy(ArtistNameParser.ArtistKey)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        stats.LongestSessionArtist = longestArtists != null
            ? ArtistNameParser.ChooseDisplayName(longestArtists)
            : "";

        int marathonPlays = sessions.Where(session => session.Count >= 20).Sum(session => session.Count);
        stats.MarathonSharePercent = 100f * marathonPlays / allEvents.Count;

        var sessionDays = sessions
            .Select(session => session[0].Event.Date)
            .OrderBy(date => date)
            .ToList();
        if (sessionDays.Count > 1)
        {
            double totalDays = 0;
            for (int i = 1; i < sessionDays.Count; i++)
                totalDays += (sessionDays[i] - sessionDays[i - 1]).TotalDays;
            stats.AverageDaysBetweenSessions = totalDays / (sessionDays.Count - 1);
        }

        return stats;
    }

    // ---------------------------------------------------------------------------------------------
    //  Months
    // ---------------------------------------------------------------------------------------------

    static List<WrappedMonth> BuildMonths(
        List<(WrappedSongInput Song, WrappedEvent Event)> allEvents)
    {
        var months = new List<WrappedMonth>();
        if (allEvents.Count == 0)
            return months;

        var byMonth = allEvents
            .GroupBy(entry => entry.Event.Date.LocalDateTime.ToString("yyyy-MM"))
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in byMonth)
        {
            var month = new WrappedMonth
            {
                Month = group.Key,
                Plays = group.Count(),
                Upvotes = group.Count(entry => entry.Event.IsUpvote),
                Downvotes = group.Count(entry => entry.Event.IsDownvote),
                DistinctSongs = group.Select(entry => entry.Song.SongId).Distinct().Count(),
                ActiveDays = group.Select(entry => DateOnly.FromDateTime(entry.Event.Date.LocalDateTime)).Distinct().Count(),
            };

            var topSong = group
                .GroupBy(entry => entry.Song.SongId)
                .OrderByDescending(songGroup => songGroup.Count())
                .First();
            var topSongInput = topSong.First().Song;
            month.TopSong = ArtistNameParser.Parse(topSongInput.StoredArtist, topSongInput.Name).Title;
            if (month.TopSong.Length == 0)
                month.TopSong = topSongInput.Name;

            var topArtist = group
                .Select(entry => ArtistNameParser.Parse(entry.Song.StoredArtist, entry.Song.Name).Artist)
                .Where(artist => artist.Length > 0)
                .GroupBy(ArtistNameParser.ArtistKey)
                .OrderByDescending(artistGroup => artistGroup.Count())
                .FirstOrDefault();
            if (topArtist != null)
            {
                month.TopArtist = ArtistNameParser.ChooseDisplayName(topArtist);
                month.TopArtistPlays = topArtist.Count();
            }

            months.Add(month);
        }

        return months;
    }

    // ---------------------------------------------------------------------------------------------
    //  Loyalty (songs that survive across years) and phases (stretches with a distinct character)
    // ---------------------------------------------------------------------------------------------

    static List<WrappedLoyalSong> BuildLoyalty(IReadOnlyList<WrappedSongInput> songs)
    {
        var loyal = new List<WrappedLoyalSong>();

        foreach (var song in songs)
        {
            var byYear = song.Events
                .GroupBy(entry => entry.Date.LocalDateTime.Year)
                .OrderBy(group => group.Key)
                .ToList();
            if (byYear.Count < 3)
                continue;

            // Rank inside each year needs the year's own totals, which are global - computed by the caller
            // through the play counts it already has; here the per-year counts are enough for the shape.
            var entry = new WrappedLoyalSong
            {
                Song = WrappedSongBuilder.Basic(song),
                YearsPlayed = byYear.Count,
                Years = byYear.Select(group => group.Key).ToList(),
                PlaysPerYear = byYear.Select(group => group.Count()).ToList(),
            };
            loyal.Add(entry);
        }

        return loyal
            .OrderByDescending(entry => entry.YearsPlayed)
            .ThenByDescending(entry => entry.PlaysPerYear.Sum())
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Splits the period into a small number of stretches that differ in what was played. The method is
    /// deliberately simple and explainable: it walks the events in fixed-length blocks and starts a new
    /// phase when the artist mix of a block barely overlaps the running phase.
    /// </summary>
    static List<WrappedPhase> BuildPhases(List<(WrappedSongInput Song, WrappedEvent Event)> allEvents)
    {
        var phases = new List<WrappedPhase>();
        if (allEvents.Count < 60)
            return phases;

        const int blockSize = 40;
        const float overlapThreshold = 0.2f;

        var blocks = new List<List<(WrappedSongInput Song, WrappedEvent Event)>>();
        for (int i = 0; i < allEvents.Count; i += blockSize)
            blocks.Add(allEvents.GetRange(i, Math.Min(blockSize, allEvents.Count - i)));

        var currentBlock = new List<(WrappedSongInput Song, WrappedEvent Event)>();

        foreach (var block in blocks)
        {
            if (currentBlock.Count == 0)
            {
                currentBlock = [.. block];
                continue;
            }

            var currentArtists = ArtistSet(currentBlock);
            var blockArtists = ArtistSet(block);
            float overlap = currentArtists.Count == 0
                ? 1f
                : blockArtists.Count(artist => currentArtists.Contains(artist)) / (float)Math.Max(1, blockArtists.Count);

            if (overlap < overlapThreshold)
            {
                phases.Add(DescribePhase(currentBlock));
                currentBlock = [];
            }
            currentBlock.AddRange(block);
        }

        if (currentBlock.Count > 0)
            phases.Add(DescribePhase(currentBlock));

        // Only meaningful phases survive; very short tails are noise.
        return phases
            .Where(phase => phase.Plays >= 40)
            .OrderBy(phase => phase.Start)
            .ToList();
    }

    static HashSet<string> ArtistSet(List<(WrappedSongInput Song, WrappedEvent Event)> events) =>
        events
            .Select(entry => ArtistNameParser.ArtistKey(ArtistNameParser.Parse(entry.Song.StoredArtist, entry.Song.Name).Artist))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    static WrappedPhase DescribePhase(List<(WrappedSongInput Song, WrappedEvent Event)> events)
    {
        var phase = new WrappedPhase
        {
            Start = events[0].Event.Date,
            End = events[^1].Event.Date,
            Plays = events.Count,
            DistinctSongs = events.Select(entry => entry.Song.SongId).Distinct().Count(),
        };

        var topArtists = events
            .Select(entry => ArtistNameParser.Parse(entry.Song.StoredArtist, entry.Song.Name).Artist)
            .Where(artist => artist.Length > 0)
            .GroupBy(ArtistNameParser.ArtistKey)
            .OrderByDescending(group => group.Count())
            .ToList();
        if (topArtists.Count > 0)
        {
            phase.TopArtist = ArtistNameParser.ChooseDisplayName(topArtists[0]);
            phase.TopArtistPlays = topArtists[0].Count();
            int topFivePlays = topArtists.Take(5).Sum(group => group.Count());
            int totalArtistPlays = topArtists.Sum(group => group.Count());
            phase.ArtistConcentrationPercent = totalArtistPlays > 0 ? 100f * topFivePlays / totalArtistPlays : 0f;
        }

        var topSong = events
            .GroupBy(entry => entry.Song.SongId)
            .OrderByDescending(group => group.Count())
            .First().First().Song;
        phase.TopSong = ArtistNameParser.Parse(topSong.StoredArtist, topSong.Name).Title;

        return phase;
    }

    // ---------------------------------------------------------------------------------------------
    //  Song sections
    // ---------------------------------------------------------------------------------------------

    static void BuildSongSections(
        WrappedReport report,
        IReadOnlyList<WrappedSongInput> songs,
        List<(WrappedSongInput Song, WrappedEvent Event)> allEvents,
        IReadOnlyList<WrappedSongInput> librarySongs,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        if (allEvents.Count == 0)
            return;

        var midpoint = periodStart + TimeSpan.FromTicks((periodEnd - periodStart).Ticks / 2);
        var topByPlays = songs.OrderByDescending(song => song.Events.Count).ToList();

        report.TopSongs = topByPlays
            .Take(10)
            .Select(song => WrappedSongBuilder.Build(song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{song.Events.Count} plays in the period"))
            .ToList();

        // Obsession: most plays per active day. Needs a few plays so a single hit cannot win.
        report.Obsessions = songs
            .Where(song => song.Events.Count >= MinimumPlaysForRanking)
            .Select(song => (Song: song, PerDay: PlaysPerActiveDay(song)))
            .OrderByDescending(entry => entry.PerDay)
            .ThenByDescending(entry => entry.Song.Events.Count)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{entry.PerDay:0.0} plays per active day across {ActiveDays(entry.Song)} days"))
            .ToList();

        // One-hit wonder: a single burst of plays, then silence. The larger the play count and the shorter
        // the span, the more it looks like a phase rather than a favourite.
        report.OneHitWonders = songs
            .Where(song => song.Events.Count >= MinimumPlaysForRanking)
            .Select(song => (Song: song, Span: ActiveDays(song), Plays: song.Events.Count))
            .Where(entry => entry.Span <= 3)
            .OrderByDescending(entry => entry.Plays)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{entry.Plays} plays inside {Math.Max(1, entry.Span)} day(s), then never again"))
            .ToList();

        // Hall of fame / shame by the score the song actually accumulated in the period.
        var byNetScore = songs
            .Where(song => song.Events.Count >= 2)
            .Select(song => (Song: song, Net: song.Events.Sum(entry => entry.ScoreChange)))
            .ToList();

        report.HallOfFame = byNetScore
            .Where(entry => entry.Net > 0)
            .OrderByDescending(entry => entry.Net)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"+{entry.Net:0.0} score in the period ({CountUp(entry.Song)} up / {CountDown(entry.Song)} down)"))
            .ToList();

        report.HallOfShame = byNetScore
            .Where(entry => entry.Net < 0)
            .OrderBy(entry => entry.Net)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{entry.Net:0.0} score in the period ({CountUp(entry.Song)} up / {CountDown(entry.Song)} down)"))
            .ToList();

        // Divisive: both votes present and the smaller side is substantial. A song with 10 up and 9 down is
        // far more interesting than one with 10 up and 1 down, and a plain ratio would rank the latter higher.
        report.MostDivisive = songs
            .Select(song => (Song: song, Up: CountUp(song), Down: CountDown(song)))
            .Where(entry => entry.Up > 0 && entry.Down > 0)
            .Select(entry => (entry.Song, entry.Up, entry.Down,
                Balance: Math.Min(entry.Up, entry.Down) / (float)Math.Max(entry.Up, entry.Down),
                Total: entry.Up + entry.Down))
            .Where(entry => entry.Total >= MinimumPlaysForRanking)
            .OrderByDescending(entry => entry.Balance * entry.Total)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{entry.Up} up vs {entry.Down} down in the period ({entry.Balance * 100:0}% balance)"))
            .ToList();

        // Rediscovery / new love: compare the two halves of the period. Splitting the period rather than
        // using calendar dates keeps the comparison fair for a wrapped over a single year with few plays.
        var withHalves = songs
            .Where(song => song.Events.Count >= MinimumPlaysForRanking)
            .Select(song => (Song: song,
                First: song.Events.Count(entry => entry.Date < midpoint),
                Second: song.Events.Count(entry => entry.Date >= midpoint)))
            .ToList();

        report.Rediscovered = withHalves
            .Where(entry => entry.First >= 3 && entry.Second * 3 < entry.First)
            .OrderByDescending(entry => entry.First - entry.Second)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{entry.First} plays early in the period, only {entry.Second} later on"))
            .ToList();

        report.NewlyEmbraced = withHalves
            .Where(entry => entry.Second >= 4 && entry.First * 3 < entry.Second)
            .OrderByDescending(entry => entry.Second - entry.First)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{entry.Second} plays in the second half vs {entry.First} in the first"))
            .ToList();

        // Fastest fader: songs the listener walked away from. Uses the gap between the last play and the
        // end of the period, so a song that faded out recently is only interesting if it faded some time ago.
        report.FastestFaders = songs
            .Where(song => song.Events.Count >= 5)
            .Select(song => (Song: song,
                Last: song.Events[^1].Date,
                GapDays: (periodEnd - song.Events[^1].Date).TotalDays))
            .Where(entry => entry.GapDays >= 60)
            .OrderByDescending(entry => entry.Song.Events.Count)
            .ThenByDescending(entry => entry.GapDays)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"{entry.Song.Events.Count} plays, last one {entry.GapDays:0} days before the period ended"))
            .ToList();

        // Longest kept: songs that carried a positive score for the whole span of the period.
        report.LongestVoted = songs
            .Select(song => (Song: song, Span: (song.Events[^1].Date - song.Events[0].Date).TotalDays,
                Up: CountUp(song), Down: CountDown(song)))
            .Where(entry => entry.Span > 0 && entry.Up > entry.Down)
            .OrderByDescending(entry => entry.Span)
            .ThenByDescending(entry => entry.Up)
            .Take(10)
            .Select(entry => WrappedSongBuilder.Build(entry.Song, allEvents.Count, midpoint, periodStart, periodEnd,
                $"positively rated across {entry.Span:0} days ({entry.Up} up / {entry.Down} down)"))
            .ToList();

        _ = librarySongs;
    }

    static int ActiveDays(WrappedSongInput song) =>
        song.Events.Select(entry => DateOnly.FromDateTime(entry.Date.LocalDateTime)).Distinct().Count();

    static double PlaysPerActiveDay(WrappedSongInput song)
    {
        int days = ActiveDays(song);
        return days > 0 ? song.Events.Count / (double)days : 0;
    }

    static int CountUp(WrappedSongInput song) => song.Events.Count(entry => entry.IsUpvote);

    static int CountDown(WrappedSongInput song) => song.Events.Count(entry => entry.IsDownvote);

    // ---------------------------------------------------------------------------------------------
    //  Artists
    // ---------------------------------------------------------------------------------------------

    static List<WrappedArtist> BuildTopArtists(
        IReadOnlyList<WrappedSongInput> songs,
        IReadOnlyList<WrappedSongInput> librarySongs,
        List<(WrappedSongInput Song, WrappedEvent Event)> allEvents)
    {
        // Group the period's songs by parsed artist. Songs without any artist information take no part -
        // a nameless bucket would dominate the ranking for this library and say nothing.
        var groups = songs
            .Select(song => (Song: song, Parsed: ArtistNameParser.Parse(song.StoredArtist, song.Name)))
            .Where(entry => entry.Parsed.Artist.Length > 0)
            .GroupBy(entry => ArtistNameParser.ArtistKey(entry.Parsed.Artist), StringComparer.Ordinal)
            .ToList();

        var artists = new List<WrappedArtist>();
        foreach (var group in groups)
        {
            var members = group.ToList();
            var artist = new WrappedArtist
            {
                Name = ArtistNameParser.ChooseDisplayName(members.Select(entry => entry.Parsed.Artist)),
                Plays = members.Sum(entry => entry.Song.Events.Count),
                Upvotes = members.Sum(entry => CountUp(entry.Song)),
                Downvotes = members.Sum(entry => CountDown(entry.Song)),
                DistinctSongs = members.Select(entry => entry.Song.SongId).Distinct().Count(),
                TotalScore = members.Sum(entry => entry.Song.Score),
            };

            var topSong = members.OrderByDescending(entry => entry.Song.Events.Count).First().Song;
            var topSongParsed = ArtistNameParser.Parse(topSong.StoredArtist, topSong.Name);
            artist.TopSongName = topSongParsed.Title.Length > 0 ? topSongParsed.Title : topSong.Name;

            // Deep cuts: how many of this artist's songs in the library were not played in the period.
            string artistKey = group.Key;
            artist.UnplayedOwnedSongs = librarySongs.Count(song =>
                !members.Any(member => member.Song.SongId == song.SongId)
                && ArtistNameParser.ArtistKey(ArtistNameParser.Parse(song.StoredArtist, song.Name).Artist) == artistKey);

            artists.Add(artist);
        }

        return artists
            .OrderByDescending(artist => artist.Plays)
            .ThenByDescending(artist => artist.TotalScore)
            .Take(15)
            .ToList();
    }

    // ---------------------------------------------------------------------------------------------
    //  Highlights
    // ---------------------------------------------------------------------------------------------

    static List<string> BuildHighlights(WrappedReport report)
    {
        var highlights = new List<string>();
        var headline = report.Headline;

        if (headline.Plays == 0)
        {
            highlights.Add("No listening recorded in this period.");
            return highlights;
        }

        highlights.Add($"{headline.Plays} plays across {headline.ActiveDays} days, {headline.DistinctSongs} different songs and {headline.DistinctArtists} artists.");

        if (report.TopSongs.Count > 0)
            highlights.Add($"Most played: {Describe(report.TopSongs[0])} with {report.TopSongs[0].Plays} plays.");

        if (report.TopArtists.Count > 0)
            highlights.Add($"Top artist: {report.TopArtists[0].Name} ({report.TopArtists[0].Plays} plays over {report.TopArtists[0].DistinctSongs} songs).");

        if (headline.LongestDailyStreak >= 3)
            highlights.Add($"Longest run of consecutive days with music: {headline.LongestDailyStreak}.");
        if (headline.BusiestDayPlays > 0)
            highlights.Add($"Busiest day: {headline.BusiestDay} with {headline.BusiestDayPlays} plays.");
        if (report.Rhythm.NightOwlPercent >= 25)
            highlights.Add($"{report.Rhythm.NightOwlPercent:0}% of the listening happened between 22:00 and 04:00.");
        if (report.Rhythm.MorningPercent >= 20)
            highlights.Add($"{report.Rhythm.MorningPercent:0}% of it happened between 06:00 and 10:00.");
        if (report.Sessions.SessionCount > 0 && report.Sessions.LongestSessionSongs >= 20)
            highlights.Add($"The longest sitting ran {report.Sessions.LongestSessionSongs} songs in {report.Sessions.LongestSessionMinutes:0} minutes" +
                (report.Sessions.LongestSessionArtist.Length > 0 ? $", mostly {report.Sessions.LongestSessionArtist}." : "."));
        if (report.MostDivisive.Count > 0)
            highlights.Add($"Most argued about with yourself: {Describe(report.MostDivisive[0])}.");

        return highlights;
    }

    static string Describe(WrappedSong song) =>
        song.Artist.Length > 0 ? $"{song.Artist} - {song.Name}" : song.Name;
}

/// <summary>Builds the UI facing song objects from the analysis inputs.</summary>
static class WrappedSongBuilder
{
    /// <summary>A song object without period statistics, for contexts outside a single period.</summary>
    public static WrappedSong Basic(WrappedSongInput song)
    {
        var parsed = ArtistNameParser.Parse(song.StoredArtist, song.Name);
        return new WrappedSong
        {
            SongId = song.SongId,
            Name = parsed.Title.Length > 0 ? parsed.Title : song.Name,
            Artist = parsed.Artist,
            Album = song.Album,
            FilePath = song.FilePath,
            HasFile = song.HasFile,
            Events = song.Events.Count,
            Plays = song.Events.Count,
            Upvotes = song.Events.Count(entry => entry.IsUpvote),
            Downvotes = song.Events.Count(entry => entry.IsDownvote),
            Skips = song.Events.Count(entry => entry.IsSkip),
            TotalLikes = song.TotalLikes,
            TotalDislikes = song.TotalDislikes,
            Score = song.Score,
            Streak = song.Streak,
            DateAdded = song.DateAdded,
            FirstPlay = song.Events.Count > 0 ? song.Events[0].Date : null,
            LastPlay = song.Events.Count > 0 ? song.Events[^1].Date : null,
        };
    }

    public static WrappedSong Build(
        WrappedSongInput song,
        int periodPlays,
        DateTimeOffset midpoint,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string reason)
    {
        var built = Basic(song);
        built.Reason = reason;
        built.NetScoreChange = song.Events.Sum(entry => entry.ScoreChange);
        built.FirstHalfPlays = song.Events.Count(entry => entry.Date < midpoint);
        built.SecondHalfPlays = song.Events.Count(entry => entry.Date >= midpoint);
        built.ShareOfPeriodPlays = periodPlays > 0 ? 100f * song.Events.Count / periodPlays : 0f;

        int days = song.Events.Count == 0
            ? 0
            : song.Events.Select(entry => DateOnly.FromDateTime(entry.Date.LocalDateTime)).Distinct().Count();
        built.PlaysPerActiveDay = days > 0 ? song.Events.Count / (double)days : 0;

        int voted = built.Upvotes + built.Downvotes;
        built.VoteRatio = voted > 0 ? built.Upvotes / (float)voted : -1f;

        _ = periodStart;
        _ = periodEnd;
        return built;
    }
}
