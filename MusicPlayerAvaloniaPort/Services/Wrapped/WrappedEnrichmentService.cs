using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayerAvaloniaPort.Persistence;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>Release information found online for one song.</summary>
public sealed class WrappedEnrichmentEntry
{
    public Guid SongId { get; set; }
    /// <summary>Normalized artist/title the match was made for, so a renamed file invalidates the entry.</summary>
    public string MatchKey { get; set; } = "";
    public string MusicBrainzId { get; set; } = "";
    /// <summary>Year of the earliest release MusicBrainz knows for this recording (0 = unknown).</summary>
    public int FirstReleaseYear { get; set; }
    public string ReleaseTitle { get; set; } = "";
    /// <summary>MusicBrainz' own descriptive tags of the recording (often empty).</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>0..1 confidence of the match; only high confidence results are ever stored.</summary>
    public float Confidence { get; set; }
    public DateTimeOffset LookedUpAt { get; set; }
}

/// <summary>
/// Optional online enrichment for the wrapped: asks MusicBrainz for the release year of songs whose file
/// name matches a recording confidently.
/// <para>
/// This is deliberately the <i>smallest</i> possible online component, because most of this library is not
/// in any database: a song is only accepted when both the title and the artist match a result closely
/// (a fuzzy comparison, not a search hit), and everything else is simply left without a release year. The
/// audio analysis is what describes those songs; this adds the one thing a decoder cannot know - when the
/// music was released.
/// </para>
/// <para>
/// It is rate limited to one request per second as MusicBrainz requires, caches everything it finds (also
/// the negative results), and stops after a budget of requests so it can never turn into a runaway scan of
/// a 3 500 song library. Progress is reported so a long run stays cancellable.
/// </para>
/// </summary>
public sealed class WrappedEnrichmentService
{
    /// <summary>MusicBrainz asks for a descriptive user agent with contact information.</summary>
    public const string UserAgent = "MusicPlayerWrapped/1.0 (https://github.com/music-player-sync)";

    const string BaseUrl = "https://musicbrainz.org/ws/2/recording";
    /// <summary>MusicBrainz allows one request per second; a little headroom avoids being throttled.</summary>
    static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(1100);

    /// <summary>
    /// How often a throttled (503/429) request is retried before the run gives up on the lookup. MusicBrainz
    /// asks for a wait rather than refusing outright, so retrying is the correct reaction - but a service
    /// that keeps refusing must not hang the run forever.
    /// </summary>
    const int MaximumThrottleRetries = 5;
    /// <summary>Neither the title nor the artist similarity may be below this, whatever the total is.</summary>
    const double MinimumTitleSimilarity = 0.7;
    const double MinimumArtistSimilarity = 0.6;
    /// <summary>Combined confidence a match must reach to be stored.</summary>
    const float MinimumConfidence = 0.82f;

    sealed class EnrichmentDocument
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, WrappedEnrichmentEntry> Entries { get; set; } = new();
        /// <summary>Lookups that found nothing, so they are not repeated.</summary>
        public HashSet<string> Misses { get; set; } = new(StringComparer.Ordinal);
    }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    readonly HttpClient httpClient;
    readonly Dictionary<string, WrappedEnrichmentEntry> entries = new(StringComparer.Ordinal);
    readonly HashSet<string> misses = new(StringComparer.Ordinal);
    DateTimeOffset lastRequest = DateTimeOffset.MinValue;
    bool dirty;

    public WrappedEnrichmentService() : this(new HttpClientHandler())
    {
    }

    /// <summary>
    /// Uses the given HTTP handler. This exists so the retry behaviour can be tested with a stub handler -
    /// rate limiting is the part that cannot be exercised against the real service from a build machine.
    /// </summary>
    internal WrappedEnrichmentService(HttpMessageHandler handler)
    {
        httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Load();
    }

    public int CachedMatches => entries.Count;
    public int CachedMisses => misses.Count;

    /// <summary>
    /// Why the last run stopped early ("" when it finished normally). Surfaced into the report: a lookup
    /// that fails at the very first request produces exactly the same "nothing was matched" as a lookup that
    /// genuinely found nothing, and those two need very different reactions from the user.
    /// </summary>
    public string LastError { get; private set; } = "";

    /// <summary>Requests actually sent to MusicBrainz in the last run (a failed request counts).</summary>
    public int RequestsSent { get; private set; }

    /// <summary>
    /// Looks up the given songs, at most <paramref name="budget"/> requests. Returns how many songs ended
    /// up with release information and whether the budget ran out.
    /// </summary>
    public async Task<(int Matched, bool BudgetExhausted)> EnrichAsync(
        IReadOnlyList<(Guid SongId, string Artist, string Title)> songs,
        int budget,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        int matched = 0;
        int requests = 0;
        int processed = 0;
        LastError = "";
        RequestsSent = 0;
        ThrottleWaits = 0;
        LastThrottleWait = TimeSpan.Zero;

        foreach (var song in songs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (song.Title.Length == 0)
                continue;

            string key = MatchKey(song.Artist, song.Title);
            if (entries.TryGetValue(key, out var cached))
            {
                matched++;
                processed++;
                progress?.Invoke(processed, songs.Count);
                continue;
            }
            if (misses.Contains(key))
            {
                processed++;
                progress?.Invoke(processed, songs.Count);
                continue;
            }

            if (requests >= budget)
                return (matched, true);

            requests++;
            RequestsSent = requests;
            processed++;
            try
            {
                var found = await LookupAsync(song.Artist, song.Title, cancellationToken);
                if (found != null)
                {
                    found.SongId = song.SongId;
                    found.MatchKey = key;
                    entries[key] = found;
                    matched++;
                }
                else
                {
                    misses.Add(key);
                }
                dirty = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Offline, rate limited, blocked by a proxy, malformed answer: the wrapped simply goes
                // without release years. The reason is recorded because "0 of 3445 matched" otherwise looks
                // like a matching problem when it is really a network one.
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"Wrapped: online enrichment stopped after {requests} request(s): {LastError}");
                Save();
                return (matched, true);
            }

            progress?.Invoke(processed, songs.Count);
        }

        Save();
        return (matched, false);
    }

    /// <summary>The release year found for a song, or 0 when nothing was found or the song changed.</summary>
    public int ReleaseYearFor(Guid songId, string artist, string title)
    {
        string key = MatchKey(artist, title);
        if (entries.TryGetValue(key, out var entry) && entry.SongId == songId)
            return entry.FirstReleaseYear;
        return 0;
    }

    static string MatchKey(string artist, string title) =>
        $"{ArtistNameParser.ArtistKey(artist)}|{ArtistNameParser.ArtistKey(title)}";

    /// <summary>
    /// One MusicBrainz search, then a strict comparison of the returned recordings against what the file
    /// name claims. The search's own relevance score is not trusted: a query for an obscure song always
    /// returns *something*, and accepting that would tag the wrapped with invented release years.
    /// </summary>
    async Task<WrappedEnrichmentEntry?> LookupAsync(string artist, string title, CancellationToken cancellationToken)
    {
        await RespectRateLimitAsync(cancellationToken);

        string query = artist.Length > 0
            ? $"recording:\"{Escape(title)}\" AND artist:\"{Escape(artist)}\""
            : $"recording:\"{Escape(title)}\"";
        string url = $"{BaseUrl}?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";

        string body = await GetWithBackoffAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("recordings", out var recordings))
            return null;

        WrappedEnrichmentEntry? best = null;
        float bestConfidence = 0f;

        foreach (var recording in recordings.EnumerateArray())
        {
            string candidateTitle = recording.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
            double titleSimilarity = Similarity(title, candidateTitle);
            if (titleSimilarity < MinimumTitleSimilarity)
                continue;

            double bestArtistSimilarity = 0.0;
            if (recording.TryGetProperty("artist-credit", out var credits))
            {
                foreach (var credit in credits.EnumerateArray())
                {
                    string name = credit.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    double similarity = Similarity(artist, name);
                    if (similarity > bestArtistSimilarity)
                        bestArtistSimilarity = similarity;
                }
            }

            if (artist.Length > 0 && bestArtistSimilarity < MinimumArtistSimilarity)
                continue;

            // Weighted so a perfect title with no artist evidence cannot pass on its own.
            float confidence = artist.Length > 0
                ? (float)(0.6 * titleSimilarity + 0.4 * bestArtistSimilarity)
                : (float)(0.8 * titleSimilarity + 0.2);

            if (confidence < MinimumConfidence || confidence <= bestConfidence)
                continue;

            int releaseYear = 0;
            string releaseTitle = "";
            if (recording.TryGetProperty("releases", out var releases))
            {
                var years = new List<(int Year, string Title)>();
                foreach (var release in releases.EnumerateArray())
                {
                    string date = release.TryGetProperty("date", out var dateElement) ? dateElement.GetString() ?? "" : "";
                    if (date.Length >= 4 && int.TryParse(date[..4], out int year))
                    {
                        string name = release.TryGetProperty("title", out var releaseTitleElement) ? releaseTitleElement.GetString() ?? "" : "";
                        years.Add((year, name));
                    }
                }
                if (years.Count > 0)
                {
                    var earliest = years.OrderBy(entry => entry.Year).First();
                    releaseYear = earliest.Year;
                    releaseTitle = earliest.Title;
                }
            }

            var tags = new List<string>();
            if (recording.TryGetProperty("tags", out var tagElements))
                foreach (var tag in tagElements.EnumerateArray())
                {
                    if (tag.TryGetProperty("name", out var tagName) && tagName.GetString() is string tagValue && tagValue.Length > 0)
                        tags.Add(tagValue);
                }

            best = new WrappedEnrichmentEntry
            {
                MusicBrainzId = recording.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "",
                FirstReleaseYear = releaseYear,
                ReleaseTitle = releaseTitle,
                Tags = tags.Take(5).ToList(),
                Confidence = confidence,
                LookedUpAt = DateTimeOffset.Now,
            };
            bestConfidence = confidence;
        }

        return best;
    }

    /// <summary>
    /// Fetches a URL, waiting out MusicBrainz' rate limiting instead of failing on it.
    /// <para>
    /// MusicBrainz answers <b>503 Service Unavailable</b> (with a <c>Retry-After</c> header) when its
    /// one-request-per-second window is exceeded - that is its throttling response, not an outage. Treating
    /// it as a hard error is what made a lookup stop after nine requests and report that "nothing could be
    /// matched", when in reality it simply needed to slow down. 429 is handled the same way, any other
    /// status is a real error and propagates.
    /// </para>
    /// </summary>
    async Task<string> GetWithBackoffAsync(string url, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            await RespectRateLimitAsync(cancellationToken);

            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(cancellationToken);

            bool throttled = response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                          || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
            if (!throttled || attempt > MaximumThrottleRetries)
                response.EnsureSuccessStatusCode(); // throws with the status in the message

            TimeSpan wait = BackoffFor(attempt, response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
            RecordThrottleWait(wait);
            await Task.Delay(wait, cancellationToken);
        }
    }

    /// <summary>
    /// How long to wait before retrying a throttled request.
    /// <para>
    /// Split out from the HTTP call so it can be tested: this is the decision that was wrong (a throttled
    /// answer was treated as a failure), and it cannot be exercised against the real service from a build
    /// machine. The server's own <c>Retry-After</c> is honoured when present; otherwise the wait doubles per
    /// attempt. The result is clamped to 1..60 seconds so neither a missing header nor an absurd one can
    /// stall a run.
    /// </para>
    /// </summary>
    internal static TimeSpan BackoffFor(int attempt, TimeSpan? retryAfterDelta, DateTimeOffset? retryAfterDate)
    {
        TimeSpan wait = retryAfterDelta
            ?? (retryAfterDate is DateTimeOffset date ? date - DateTimeOffset.Now : (TimeSpan?)null)
            ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

        if (wait < TimeSpan.FromSeconds(1))
            wait = TimeSpan.FromSeconds(1);
        if (wait > TimeSpan.FromSeconds(60))
            wait = TimeSpan.FromSeconds(60);
        return wait;
    }

    internal void RecordThrottleWait(TimeSpan wait)
    {
        ThrottleWaits++;
        LastThrottleWait = wait;
    }

    /// <summary>How many times the lookup had to wait for MusicBrainz' rate limiting.</summary>
    public int ThrottleWaits { get; private set; }

    /// <summary>The last wait MusicBrainz asked for (for the report's notes).</summary>
    public TimeSpan LastThrottleWait { get; private set; }

    async Task RespectRateLimitAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.Now - lastRequest;
        if (since < MinimumRequestInterval)
            await Task.Delay(MinimumRequestInterval - since, cancellationToken);
        lastRequest = DateTimeOffset.Now;
    }

    static string Escape(string value) => value.Replace("\\", "").Replace("\"", "");

    /// <summary>
    /// Similarity of two names in 0..1: a Levenshtein ratio, with a boost when one name contains the other
    /// as a whole word ("Kanon" vs "Canon Kanon ver."). Deliberately simple and predictable - a clever
    /// matcher that occasionally pairs the wrong recordings would silently poison the release statistics.
    /// </summary>
    static double Similarity(string first, string second)
    {
        string a = Normalize(first);
        string b = Normalize(second);
        if (a.Length == 0 || b.Length == 0)
            return 0.0;
        if (a == b)
            return 1.0;

        int distance = Levenshtein(a, b);
        double ratio = 1.0 - distance / (double)Math.Max(a.Length, b.Length);

        // Containment boost: "wasted love" inside "wasted love (eurovision version)".
        if (a.Length >= 4 && b.Contains(a, StringComparison.Ordinal) || b.Length >= 4 && a.Contains(b, StringComparison.Ordinal))
            ratio = Math.Max(ratio, 0.85);

        return Math.Clamp(ratio, 0.0, 1.0);
    }

    static string Normalize(string value) =>
        new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static int Levenshtein(string first, string second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (int j = 0; j <= second.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= first.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= second.Length; j++)
            {
                int cost = first[i - 1] == second[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[second.Length];
    }

    // ---------------------------------------------------------------------------------------------
    //  Cache
    // ---------------------------------------------------------------------------------------------

    void Load()
    {
        try
        {
            string path = EnrichmentCachePath;
            if (!File.Exists(path))
                return;

            var document = JsonSerializer.Deserialize<EnrichmentDocument>(File.ReadAllText(path), JsonOptions);
            if (document == null)
                return;

            foreach (var pair in document.Entries)
                entries[pair.Key] = pair.Value;
            foreach (string miss in document.Misses)
                misses.Add(miss);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the wrapped online cache (will look up again): {ex.Message}");
        }
    }

    public void Save()
    {
        if (!dirty)
            return;

        try
        {
            string directory = PersistenceLocations.WrappedDirectory;
            Directory.CreateDirectory(directory);
            var document = new EnrichmentDocument
            {
                Entries = new Dictionary<string, WrappedEnrichmentEntry>(entries, StringComparer.Ordinal),
                Misses = new HashSet<string>(misses, StringComparer.Ordinal),
            };
            File.WriteAllText(EnrichmentCachePath, JsonSerializer.Serialize(document, JsonOptions));
            dirty = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write the wrapped online cache: {ex.Message}");
        }
    }

    static string EnrichmentCachePath => Path.Combine(PersistenceLocations.WrappedDirectory, "online-lookup.json");
}
