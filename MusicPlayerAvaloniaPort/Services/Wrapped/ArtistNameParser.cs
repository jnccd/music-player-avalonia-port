using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MusicPlayerAvaloniaPort.Services.Wrapped;

/// <summary>
/// Recovers an artist and a title from what the library actually has.
/// <para>
/// The vast majority of these rows carry <b>no</b> Artist/Album tags at all - the library is game and
/// anime soundtracks, YouTube rips and Eurovision entries, and the file name is the only metadata that
/// exists ("[DnB] - Bustre - Combine [Monstercat Release].mp3", "Haddaway - What Is Love.mp3",
/// "Portal 2 OST Volume 2 - I AM NOT A MORON.mp3"). Simply grouping by the Artist column would therefore
/// put nearly every song under "" and make the whole artist half of the wrapped meaningless.
/// </para>
/// <para>
/// So the file name is parsed: a leading "artist -" or a bracketed tag is preferred, the trailing YouTube
/// noise ("(Official Video)", "[HD]", a bare video id) is stripped, and the storage column wins whenever
/// it actually carries something. Every extracted artist is also flagged with how it was found, because
/// a wrong artist is worse than a missing one - the sections that rank artists can then skip the guesses.
/// </para>
/// </summary>
public static class ArtistNameParser
{
    /// <summary>Where an artist name came from.</summary>
    public enum ArtistSource
    {
        /// <summary>No artist could be determined.</summary>
        None,
        /// <summary>Found in the file name (the common case for this library).</summary>
        FileName,
        /// <summary>Read from the stored Artist tag column.</summary>
        StoredTag,
    }

    /// <summary>The parsed identity of one song.</summary>
    public readonly record struct ParsedName(string Artist, string Title, ArtistSource Source);

    /// <summary>Separators that are used as "artist - title" in file names (ASCII and typographic dashes).</summary>
    static readonly char[] DashSeparators = ['-', '–', '—', '‐', '‑', '−'];

    /// <summary>Noise that YouTube and download tools append to file names.</summary>
    static readonly Regex NoiseTagRegex = new(
        @"\[(?:official\s*(?:music\s*)?(?:video|audio)|lyrics?|hd|hq|4k|mv|pv|m/?v|audio|full\s*ver(?:sion)?|color\s*coded|sub(?:bed|s)?|eng(?:lish)?\s*sub(?:s|bed)?|romaji|kanji|japanese|中文|official)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex ParenthesisedNoiseRegex = new(
        @"\((?:official\s*(?:music\s*)?(?:video|audio)|lyrics?|hd|hq|4k|mv|pv|audio|full\s*ver(?:sion)?|color\s*coded|sub(?:bed|s)?|eng(?:lish)?\s*sub(?:s|bed)?|romaji|kanji|japanese|official|with\s*lyrics)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A bare YouTube id, usually appended after a separating character.</summary>
    static readonly Regex BareVideoIdRegex = new(@"[\s\-_]([A-Za-z0-9_-]{11})\s*$", RegexOptions.Compiled);

    /// <summary>Collapses runs of whitespace and strips separators left dangling at either end.</summary>
    static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Parses the identity of a song from its stored tags and its file name.
    /// </summary>
    /// <param name="storedArtist">The <c>Artist</c> column, which is empty for most of this library.</param>
    /// <param name="fileName">File name including the extension (as stored in <c>UpvotedSong.Name</c>).</param>
    public static ParsedName Parse(string storedArtist, string fileName)
    {
        string cleanedStoredArtist = Cleanup(storedArtist);
        if (cleanedStoredArtist.Length > 0)
        {
            // The storage column is authoritative when it has content, but the title still has to come
            // from the file name - the library does not store one.
            return new ParsedName(cleanedStoredArtist, TitleFromFileName(fileName, cleanedStoredArtist), ArtistSource.StoredTag);
        }

        string name = StripExtension(fileName);

        // A name that starts with a separator has no artist part ("- Why I Keep Going.mp3"): without this
        // the leading dash would be read as the "artist - title" separator and produce an empty artist.
        name = name.TrimStart(DashSeparators).TrimStart();
        name = WhitespaceRegex.Replace(name, " ").Trim();

        // "Artist - Title" (and the typographic dash variants). Take the first separator that has text on
        // both sides: "Eurovision Song Contest - JJ - Wasted Love" must yield "Eurovision Song Contest".
        int separatorIndex = IndexOfDashSeparator(name);
        if (separatorIndex > 1)
        {
            string candidateArtist = Cleanup(name[..separatorIndex]);
            string candidateTitle = Cleanup(name[(separatorIndex + 1)..]);
            if (candidateArtist.Length > 1 && candidateTitle.Length > 0)
                return new ParsedName(candidateArtist, candidateTitle, ArtistSource.FileName);
        }

        // "[DnB] - Bustre - Combine [Monstercat Release]": a leading bracketed tag is a category, and the
        // real artist follows the dash after it.
        var leadingTag = Regex.Match(name, @"^\[(?<tag>[^\]]{1,40})\]\s*[-–—]\s*(?<rest>.+)$");
        if (leadingTag.Success)
        {
            string rest = leadingTag.Groups["rest"].Value;
            int restSeparator = IndexOfDashSeparator(rest);
            if (restSeparator > 1)
            {
                string candidateArtist = Cleanup(rest[..restSeparator]);
                string candidateTitle = Cleanup(rest[(restSeparator + 1)..]);
                if (candidateArtist.Length > 1 && candidateTitle.Length > 0)
                    return new ParsedName(candidateArtist, candidateTitle, ArtistSource.FileName);
            }
        }

        // No artist information at all. The whole name is the title; callers decide whether a title-only
        // song can still take part in an artist ranking (it cannot, and Source says so).
        return new ParsedName("", Cleanup(name), ArtistSource.None);
    }

    /// <summary>The title part of a file name, given an artist that was taken from the tags.</summary>
    static string TitleFromFileName(string fileName, string artist)
    {
        string name = StripExtension(fileName);
        int separator = IndexOfDashSeparator(name);
        if (separator > 1)
        {
            string left = Cleanup(name[..separator]);
            // Same artist in the name and in the tags: the name is "Artist - Title" and the title is the rest.
            if (string.Equals(left, artist, StringComparison.OrdinalIgnoreCase))
                return Cleanup(name[(separator + 1)..]);

            // Different artist in the name: the tags win for the artist, but the name still holds a title.
            // Keeping the whole name would show "Other - Song" as the title of an artist that is not Other.
            string right = Cleanup(name[(separator + 1)..]);
            if (right.Length > 0)
                return right;
        }
        return Cleanup(name);
    }

    /// <summary>
    /// Normalizes a fragment: strips YouTube noise words, collapses whitespace and removes dangling
    /// separators. Returns "" for anything unusable.
    /// </summary>
    public static string Cleanup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string result = value;
        result = NoiseTagRegex.Replace(result, " ");
        result = ParenthesisedNoiseRegex.Replace(result, " ");
        result = result.Replace('_', ' ');

        // Trailing underscore/dash separated video id ("... - dQw4w9WgXcQ").
        var videoId = BareVideoIdRegex.Match(result);
        if (videoId.Success && videoId.Groups[1].Value.Length == 11)
            result = result[..videoId.Index];

        result = WhitespaceRegex.Replace(result, " ").Trim();

        // Strip separators and leftover punctuation that no longer has text around it.
        result = result.Trim(' ', '-', '–', '—', '_', '|', ',', '.', ';', ':', '~');
        result = WhitespaceRegex.Replace(result, " ").Trim();

        // Unbalanced brackets are a sign of a truncated name; drop the leftovers rather than showing them.
        result = RemoveUnbalancedBrackets(result);

        return result;
    }

    static string RemoveUnbalancedBrackets(string value)
    {
        int openRound = value.Count(c => c == '(');
        int closeRound = value.Count(c => c == ')');
        int openSquare = value.Count(c => c == '[');
        int closeSquare = value.Count(c => c == ']');
        if (openRound == closeRound && openSquare == closeSquare)
            return value;

        string result = value;
        if (openRound != closeRound)
        {
            result = result.Replace("(", " ").Replace(")", " ");
        }
        if (openSquare != closeSquare)
        {
            result = result.Replace("[", " ").Replace("]", " ");
        }
        return WhitespaceRegex.Replace(result, " ").Trim();
    }

    /// <summary>Removes a known audio extension; other suffixes are left alone.</summary>
    static string StripExtension(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return "";
        foreach (string extension in KnownExtensions)
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return fileName[..^extension.Length];
        return fileName;
    }

    static readonly string[] KnownExtensions = [".mp3", ".wav", ".flac", ".m4a", ".ogg", ".opus", ".wma", ".aac", ".aiff"];

    /// <summary>
    /// Index of the first " - " style separator whose left side is long enough to be an artist name.
    /// Requires whitespace around the dash so hyphenated words ("Buck-Tick", "Drum-n-Bass") are not split.
    /// </summary>
    static int IndexOfDashSeparator(string value)
    {
        for (int i = 1; i < value.Length - 1; i++)
        {
            if (Array.IndexOf(DashSeparators, value[i]) < 0)
                continue;
            if (!char.IsWhiteSpace(value[i - 1]) || !char.IsWhiteSpace(value[i + 1]))
                continue;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// The grouping key for an artist: case- and whitespace-insensitive, so "Mili" and "MILI" end up as one
    /// artist. Deliberately not a fuzzy match - merging two different artists is worse than splitting one.
    /// </summary>
    public static string ArtistKey(string artist) =>
        string.IsNullOrWhiteSpace(artist) ? "" : WhitespaceRegex.Replace(artist, " ").Trim().ToUpperInvariant();

    /// <summary>
    /// Picks the spelling to display for a group of names that share an <see cref="ArtistKey"/>: the most
    /// common one, so "Mili" rather than "MILI" if the library mostly writes it that way.
    /// </summary>
    public static string ChooseDisplayName(IEnumerable<string> names)
    {
        var best = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name.Trim(), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Length)
            .Select(group => group.Key)
            .FirstOrDefault();
        return best ?? "";
    }
}
