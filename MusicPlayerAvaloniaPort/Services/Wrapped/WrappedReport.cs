using System;
using System.Collections.Generic;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// One computed "wrapped": the interesting facts about a period of listening, ready to be rendered.
/// <para>
/// It is produced by <see cref="WrappedService"/> and stored as JSON (see <see cref="WrappedStore"/>), so
/// it is written with plain, serializable types only. When this schema changes in a way that makes an old
/// file unreadable, bump <see cref="SchemaVersion"/> - the store then recomputes instead of guessing.
/// </para>
/// <para>
/// Every section carries the numbers that produced it so the UI can explain a claim instead of just
/// asserting it (e.g. a "most divisive song" entry shows the likes and dislikes it was ranked by).
/// </para>
/// </summary>
public sealed class WrappedReport
{
    /// <summary>
    /// Schema version of the persisted report; older files are recomputed rather than parsed. Bumped
    /// whenever the report grows a field that changes what a section means, so a stale file cannot render
    /// as a report with missing numbers (recomputing is cheap once the audio cache is warm).
    /// </summary>
    public const int SchemaVersion = 2;

    public int Version { get; set; } = SchemaVersion;

    /// <summary>Account this wrapped was computed for (see <c>User.UserId</c>).</summary>
    public string UserId { get; set; } = "";

    /// <summary>Display name used in the headline (falls back to the user id).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Start of the wrapped period (inclusive, local time of the entries' own timestamps).</summary>
    public DateTimeOffset PeriodStart { get; set; }
    /// <summary>End of the wrapped period (exclusive).</summary>
    public DateTimeOffset PeriodEnd { get; set; }
    /// <summary>Calendar year this wrapped covers, or null for the all-time wrapped.</summary>
    public int? Year { get; set; }
    /// <summary>Title for the UI, e.g. "2025" or "All time".</summary>
    public string PeriodLabel { get; set; } = "";

    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>Runtime of the computation that produced this file, in seconds. Shown in the UI.</summary>
    public double ComputationSeconds { get; set; }

    // ---- What was included (the UI shows this, so a wrapped is never silently partial) ----

    /// <summary>History entries inside the period.</summary>
    public int HistoryEntriesInPeriod { get; set; }
    /// <summary>History entries in the whole database, for context.</summary>
    public int HistoryEntriesTotal { get; set; }
    /// <summary>Distinct songs with at least one event in the period.</summary>
    public int SongsPlayedInPeriod { get; set; }
    /// <summary>Songs in the local database.</summary>
    public int SongsInDatabase { get; set; }
    /// <summary>Song files the library scan resolved.</summary>
    public int SongsWithFiles { get; set; }
    /// <summary>Songs this run successfully analysed from their audio.</summary>
    public int SongsAnalysed { get; set; }
    /// <summary>Songs whose audio analysis was already cached and reused.</summary>
    public int SongsFromCache { get; set; }
    /// <summary>Songs whose file was missing or unreadable.</summary>
    public int SongsUnreadable { get; set; }
    /// <summary>True when the user asked to skip the (slow) audio analysis for this run.</summary>
    public bool AudioAnalysisSkipped { get; set; }
    /// <summary>True when the online enrichment pass ran.</summary>
    public bool EnrichmentRan { get; set; }
    /// <summary>Human readable notes about anything that limited the run (online rate limits, ...).</summary>
    public List<string> Notes { get; set; } = [];

    // ---- Sections ----

    public WrappedHeadline Headline { get; set; } = new();
    public List<WrappedArtist> TopArtists { get; set; } = [];
    public List<WrappedSong> TopSongs { get; set; } = [];
    public List<WrappedSong> Obsessions { get; set; } = [];
    public List<WrappedSong> OneHitWonders { get; set; } = [];
    public List<WrappedSong> HallOfFame { get; set; } = [];
    public List<WrappedSong> HallOfShame { get; set; } = [];
    public List<WrappedSong> MostDivisive { get; set; } = [];
    public List<WrappedSong> Rediscovered { get; set; } = [];
    public List<WrappedSong> NewlyEmbraced { get; set; } = [];
    public List<WrappedSong> FastestFaders { get; set; } = [];
    public List<WrappedSong> LongestVoted { get; set; } = [];
    public List<WrappedMonth> Months { get; set; } = [];
    public WrappedListeningRhythm Rhythm { get; set; } = new();
    public WrappedSessionStats Sessions { get; set; } = new();
    public List<WrappedLoyalSong> LoyalSongs { get; set; } = [];
    public List<WrappedPhase> Phases { get; set; } = [];
    public List<WrappedSoundCluster> SoundClusters { get; set; } = [];
    public WrappedAudioProfile AudioProfile { get; set; } = new();
    public WrappedSong? SignatureSong { get; set; }
    public WrappedSong? OutlierSong { get; set; }
    public List<WrappedReleaseYear> ReleaseYears { get; set; } = [];
    public List<WrappedKeyCount> Keys { get; set; } = [];
    public List<string> Highlights { get; set; } = [];
}

/// <summary>The headline numbers of a wrapped.</summary>
public sealed class WrappedHeadline
{
    public int Plays { get; set; }
    public int Upvotes { get; set; }
    public int Downvotes { get; set; }
    public int DistinctSongs { get; set; }
    public int DistinctArtists { get; set; }
    public int ActiveDays { get; set; }
    public int DaysInPeriod { get; set; }
    /// <summary>Longest run of consecutive calendar days with at least one play.</summary>
    public int LongestDailyStreak { get; set; }
    public double PlaysPerActiveDay { get; set; }
    public float AverageVoteRatio { get; set; }
    public string BusiestDay { get; set; } = "";
    public int BusiestDayPlays { get; set; }
    /// <summary>The span the numbers actually cover, i.e. first to last entry in the period.</summary>
    public DateTimeOffset? FirstPlay { get; set; }
    public DateTimeOffset? LastPlay { get; set; }
    public double TotalListeningDays { get; set; }
}

/// <summary>An artist aggregated over the period. The artist is derived from the file name (see <see cref="ArtistNameParser"/>).</summary>
public sealed class WrappedArtist
{
    public string Name { get; set; } = "";
    public int Plays { get; set; }
    public int Upvotes { get; set; }
    public int Downvotes { get; set; }
    public int DistinctSongs { get; set; }
    /// <summary>Songs of this artist the listener owns but did not play in the period - "deep cuts awaiting".</summary>
    public int UnplayedOwnedSongs { get; set; }
    /// <summary>Score sum of the artist's songs, i.e. how much the player itself favours them.</summary>
    public float TotalScore { get; set; }
    public string TopSongName { get; set; } = "";
}

/// <summary>One song inside a wrapped section, with the numbers a ranking was based on.</summary>
public sealed class WrappedSong
{
    public Guid SongId { get; set; }
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    /// <summary>File name the library scan resolved for this song (empty when it has no file).</summary>
    public string FilePath { get; set; } = "";
    public bool HasFile { get; set; }

    /// <summary>History events (including the zero-score "played but not voted" ones).</summary>
    public int Events { get; set; }
    public int Plays { get; set; }
    public int Upvotes { get; set; }
    public int Downvotes { get; set; }
    public int Skips { get; set; }
    /// <summary>Lifetime counters from the database (they can predate the recorded history).</summary>
    public int TotalLikes { get; set; }
    public int TotalDislikes { get; set; }
    public float Score { get; set; }
    public int Streak { get; set; }
    public DateTimeOffset? DateAdded { get; set; }

    public DateTimeOffset? FirstPlay { get; set; }
    public DateTimeOffset? LastPlay { get; set; }
    /// <summary>Sum of the recorded score changes in the period.</summary>
    public float NetScoreChange { get; set; }
    /// <summary>Events per active day between the first and last play in the period.</summary>
    public double PlaysPerActiveDay { get; set; }
    /// <summary>Share of the period's plays spent on this song, in percent.</summary>
    public float ShareOfPeriodPlays { get; set; }
    /// <summary>Plays in the first / second half of the period - the basis of the rediscovery ranking.</summary>
    public int FirstHalfPlays { get; set; }
    public int SecondHalfPlays { get; set; }
    /// <summary>Vote ratio (likes / (likes + dislikes)) over the period's events, or -1 when never voted on.</summary>
    public float VoteRatio { get; set; } = -1f;

    /// <summary>A sentence explaining why this song is in its section, built from the numbers above.</summary>
    public string Reason { get; set; } = "";
}

/// <summary>One month of listening behaviour.</summary>
public sealed class WrappedMonth
{
    /// <summary>"2025-03"</summary>
    public string Month { get; set; } = "";
    public int Plays { get; set; }
    public int Upvotes { get; set; }
    public int Downvotes { get; set; }
    public int DistinctSongs { get; set; }
    public int ActiveDays { get; set; }
    /// <summary>Most played artist of that month.</summary>
    public string TopArtist { get; set; } = "";
    public int TopArtistPlays { get; set; }
    /// <summary>Most played song of that month.</summary>
    public string TopSong { get; set; } = "";
}

/// <summary>When the listening happened: hour of day, weekday and their combination.</summary>
public sealed class WrappedListeningRhythm
{
    /// <summary>Plays per hour of day, 24 entries, local time.</summary>
    public int[] ByHour { get; set; } = new int[24];
    /// <summary>Plays per weekday, Monday first.</summary>
    public int[] ByWeekday { get; set; } = new int[7];
    /// <summary>Weekday (0=Monday) x hour (0..23) matrix, for the "listening fingerprint" heatmap.</summary>
    public int[] WeekdayHour { get; set; } = new int[7 * 24];
    /// <summary>Hour with the most plays.</summary>
    public int PeakHour { get; set; }
    /// <summary>Share of plays between 22:00 and 04:00, in percent.</summary>
    public float NightOwlPercent { get; set; }
    /// <summary>Share of plays between 06:00 and 10:00, in percent. A high value means the day starts with music.</summary>
    public float MorningPercent { get; set; }
    /// <summary>Most active weekday (0=Monday).</summary>
    public int PeakWeekday { get; set; }
    /// <summary>Share of plays that landed on a weekend day, in percent.</summary>
    public float WeekendPercent { get; set; }
    /// <summary>Share of plays inside the peak hour, in percent - how concentrated the habit is.</summary>
    public float PeakHourSharePercent { get; set; }
}

/// <summary>How the listening is grouped into sittings.</summary>
public sealed class WrappedSessionStats
{
    /// <summary>Events closer together than this (minutes) belong to the same session.</summary>
    public double GapMinutes { get; set; }
    public int SessionCount { get; set; }
    public double AverageSongsPerSession { get; set; }
    public double MedianSongsPerSession { get; set; }
    public double AverageSessionMinutes { get; set; }
    public int LongestSessionSongs { get; set; }
    public double LongestSessionMinutes { get; set; }
    public DateTimeOffset? LongestSessionStart { get; set; }
    /// <summary>The artist that dominated the longest session.</summary>
    public string LongestSessionArtist { get; set; } = "";
    /// <summary>Share of plays that belong to a session of 20+ songs, in percent.</summary>
    public float MarathonSharePercent { get; set; }
    /// <summary>Average number of days between two sessions.</summary>
    public double AverageDaysBetweenSessions { get; set; }
}

/// <summary>A song that keeps coming back year after year.</summary>
public sealed class WrappedLoyalSong
{
    public WrappedSong Song { get; set; } = new();
    /// <summary>Number of distinct calendar years the song was played in.</summary>
    public int YearsPlayed { get; set; }
    /// <summary>Plays per year, oldest first - the shape of the loyalty.</summary>
    public List<int> PlaysPerYear { get; set; } = [];
    /// <summary>Years the song was played in, oldest first.</summary>
    public List<int> Years { get; set; } = [];
    /// <summary>Rank of the song inside each year (1 = most played that year); 0 = not played.</summary>
    public List<int> YearRanks { get; set; } = [];
}

/// <summary>
/// A stretch of listening with a distinct character, found by looking at what the listener played in
/// sequence (not by calendar month): the songs of a phase share artists and sound.
/// </summary>
public sealed class WrappedPhase
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int Plays { get; set; }
    public int DistinctSongs { get; set; }
    public string TopArtist { get; set; } = "";
    public int TopArtistPlays { get; set; }
    public string TopSong { get; set; } = "";
    /// <summary>Mean tempo of the phase's songs, when audio features exist.</summary>
    public float AverageBpm { get; set; }
    /// <summary>Mean loudness of the phase's songs.</summary>
    public float AverageLoudness { get; set; }
    /// <summary>Mean brightness (spectral centroid, Hz) of the phase's songs.</summary>
    public float AverageBrightnessHz { get; set; }
    /// <summary>Share of the phase's plays that stayed inside its top 5 artists, in percent.</summary>
    public float ArtistConcentrationPercent { get; set; }
    /// <summary>Readable summary of what makes this phase different.</summary>
    public string Characterization { get; set; } = "";
}

/// <summary>
/// A group of songs that sound alike, found by clustering the audio features. The label is derived from
/// the cluster's own statistics (see <see cref="SoundClusterLabeler"/>), never from an online genre.
/// </summary>
public sealed class WrappedSoundCluster
{
    public int ClusterIndex { get; set; }
    /// <summary>Short auto-generated label, e.g. "Fast, bright and busy".</summary>
    public string Label { get; set; } = "";
    /// <summary>The features that made this cluster stand out, with their deviation from the library average.</summary>
    public List<string> Characteristics { get; set; } = [];
    public int SongCount { get; set; }
    /// <summary>Share of the analysed library in this cluster, in percent.</summary>
    public float LibrarySharePercent { get; set; }
    /// <summary>Plays this cluster got in the period.</summary>
    public int Plays { get; set; }
    /// <summary>Share of the period's plays, in percent.</summary>
    public float PlaySharePercent { get; set; }
    /// <summary>Plays per song in this cluster - the clearest "do you actually like this sound" number.</summary>
    public double PlaysPerSong { get; set; }
    /// <summary>Likes minus dislikes of the cluster's songs.</summary>
    public int NetLikes { get; set; }
    public float AverageBpm { get; set; }
    public float AverageLoudness { get; set; }
    public float AverageBrightnessHz { get; set; }
    /// <summary>Most played songs of the cluster in the period.</summary>
    public List<WrappedSong> TopSongs { get; set; } = [];
    /// <summary>Most played artists of the cluster in the period.</summary>
    public List<string> TopArtists { get; set; } = [];
    /// <summary>Share of the cluster's songs that were never played in the period, in percent.</summary>
    public float UntouchedPercent { get; set; }
    /// <summary>
    /// How well separated this grouping is - the Calinski-Harabasz ratio the number of groups was chosen
    /// by. Reported so "my library is two sound worlds" can be told apart from "two is what the data
    /// supports": a low ratio means the groups are not clearly distinct, and it is also what a finer split
    /// would have to beat.
    /// </summary>
    public float Separation { get; set; }
    /// <summary>How many groups the clustering settled on, so the UI can say why it stopped there.</summary>
    public int ClusterCount { get; set; }
    /// <summary>
    /// How many independent directions of the measured sound the grouping ran on. Reported because it is
    /// the difference between "these groups are one aspect of the sound" and "these groups combine several".
    /// </summary>
    public int ComponentsUsed { get; set; }
    /// <summary>
    /// The separation a split into <see cref="ClusterCount"/>+1 groups achieved. Shown next to the chosen
    /// one, so "only two groups" is explainable: a finer split scoring lower means the library genuinely
    /// does not divide further, not that the number of groups was capped.
    /// </summary>
    public float NextSplitSeparation { get; set; }
}

/// <summary>Library-wide audio statistics, the baseline every song is compared against.</summary>
public sealed class WrappedAudioProfile
{
    public int AnalysedSongs { get; set; }
    public float AverageBpm { get; set; }
    public float MedianBpm { get; set; }
    /// <summary>BPM histogram over fixed 10 BPM buckets, starting at 60.</summary>
    public List<WrappedBpmBucket> BpmHistogram { get; set; } = [];
    public float AverageLoudness { get; set; }
    public float AverageDynamicRange { get; set; }
    public float AverageCrestFactorDb { get; set; }
    public float AverageBrightnessHz { get; set; }
    /// <summary>Average share of the song that is loud, in percent - the "loudness war" number.</summary>
    public float AverageLoudFractionPercent { get; set; }
    public float MinorSharePercent { get; set; }
    public string MostCommonKey { get; set; } = "";
    /// <summary>Mean tempo of the songs played in the period, for comparison with the library.</summary>
    public float PlayedAverageBpm { get; set; }
    /// <summary>Mean loudness of the songs played in the period.</summary>
    public float PlayedAverageLoudness { get; set; }
    /// <summary>Mean brightness of the songs played in the period.</summary>
    public float PlayedAverageBrightnessHz { get; set; }
}

/// <summary>One bar of the tempo histogram.</summary>
public sealed class WrappedBpmBucket
{
    public int FromBpm { get; set; }
    public int ToBpm { get; set; }
    public int SongCount { get; set; }
    public int PlayCount { get; set; }
}

/// <summary>How many songs the listener owns per release year (from the online enrichment).</summary>
public sealed class WrappedReleaseYear
{
    public int Year { get; set; }
    public int SongCount { get; set; }
    public int Plays { get; set; }
    public int DistinctArtists { get; set; }
}

/// <summary>How many analysed songs are in a given key.</summary>
public sealed class WrappedKeyCount
{
    public string Key { get; set; } = "";
    public int SongCount { get; set; }
    public int Plays { get; set; }
    /// <summary>Upvotes minus downvotes of the songs in this key.</summary>
    public int NetLikes { get; set; }
}
