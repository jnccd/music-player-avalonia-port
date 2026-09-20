using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Helpers;

/// <summary>
/// Platform independent helpers shared by the desktop and the mobile client: walking a song library,
/// reading the identity tags of a song file and the string distances the "play a song quickly" matching
/// uses. Platform specific helpers (SkiaSharp bitmap tinting, launching yt-dlp) live in the desktop
/// project as <c>DesktopHelperFuncs</c>.
/// </summary>
public static class HelperFuncs
{
    /// <summary>
    /// Recursively finds all mp3 files in a directory and its subdirectories.
    /// </summary>
    /// <param name="StartDir">The directory to search in.</param>
    /// <returns>Absolute paths of all mp3 files</returns>
    public static List<string> FindAllMp3FilesInDir(string StartDir)
    {
        return FindAllMp3FilesInDir(StartDir, null);
    }

    /// <summary>
    /// Recursively finds all mp3 files in a directory and its subdirectories, reporting progress while
    /// the walk is still running.
    /// </summary>
    /// <param name="StartDir">The directory to search in.</param>
    /// <param name="mp3FileFound">Optional callback invoked on the calling thread while walking - on
    /// every 25th found mp3 file, and once more with the final count after the walk finished (the count
    /// passed is monotonic). Reporting every single file would be pure overhead for progress purposes;
    /// callers see the same movement from a negligible number of calls. The final total is only known
    /// once the walk finished.</param>
    /// <returns>Absolute paths of all mp3 files</returns>
    public static List<string> FindAllMp3FilesInDir(string StartDir, Action<int>? mp3FileFound = null)
    {
        List<string> re = new();
        int filesFound = 0;
        void Walk(string dir)
        {
            foreach (string s in Directory.GetFiles(dir))
                if (s.EndsWith(".mp3"))
                {
                    re.Add(s);
                    filesFound++;
                    if (filesFound % 25 == 0)
                        mp3FileFound?.Invoke(filesFound);
                }

            foreach (string D in Directory.GetDirectories(dir))
                Walk(D);
        }
        Walk(StartDir);
        // The final partial chunk (fewer than 25 files) would otherwise never be reported.
        if (filesFound % 25 != 0)
            mp3FileFound?.Invoke(filesFound);
        return re;
    }

    public static bool DirOrSubDirsContainMp3(string StartDir)
    {
        foreach (string s in Directory.GetFiles(StartDir))
            if (s.EndsWith(".mp3"))
                return true;

        foreach (string D in Directory.GetDirectories(StartDir))
            if (DirOrSubDirsContainMp3(D))
                return true;
        return false;
    }

    /// <summary>
    /// Reads the album and the (" + "-joined) album artists of a song file, the identity convention the
    /// whole codebase uses (see <see cref="MusicPlayerSyncInterface.SongFileMatching"/>). Empty strings
    /// mean the file carries no such metadata.
    /// </summary>
    public static (string Album, string Artists) GetAlbumAndArtistsFromSong(string songPath)
    {
        // Dispose the TagLib file (and with it the underlying file stream): reading the tags of every
        // song file of a library scan must not leave thousands of open file handles behind until the
        // finalizer collects them (that caused GC/finalizer churn and sluggishness after the scan).
        using var file = TagLib.File.Create(songPath);
        return (file.Tag.Album ?? "", file.Tag.AlbumArtists.Length == 0 ? "" : file.Tag.AlbumArtists.Aggregate((x, y) => x + " + " + y));
    }

    public static float Sigmoid(double value)
    {
        return (float)(1.0 / (1.0 + Math.Pow(Math.E, -value)));
    }

    public static string Combine(this IEnumerable<string> strings, string combinator = "\n")
    {
        if (strings.Count() == 0)
            return "";
        else
            return strings.Aggregate((x, y) => $"{x}{combinator}{y}");
    }

    // String Distances
    public static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s))
        {
            if (string.IsNullOrEmpty(t))
                return 0;
            return t.Length;
        }

        if (string.IsNullOrEmpty(t))
        {
            return s.Length;
        }

        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        // initialize the top and right of the table to 0, 1, 2, ...
        for (int i = 0; i <= n; d[i, 0] = i++) ;
        for (int j = 1; j <= m; d[0, j] = j++) ;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                int min1 = d[i - 1, j] + 1;
                int min2 = d[i, j - 1] + 1;
                int min3 = d[i - 1, j - 1] + cost;
                d[i, j] = Math.Min(Math.Min(min1, min2), min3);
            }
        }
        return d[n, m];
    }

    public static float LevenshteinDistanceWrapper(string Input, string SongName)
    {
        if (Input == null || Input == "" || SongName == "" || SongName == null)
            return float.MaxValue;

        Input = Input.ToLower();
        SongName = SongName.ToLower();

        if (SongName.Length <= Input.Length)
            return LevenshteinDistance(Input, SongName);

        List<float> Distances = new List<float>();

        string[] InSplit = Input.Split(' ');

        if (InSplit.Length == 1)
        {
            string[] split = SongName.Split(' ');
            for (int i = 0; i < split.Length; i++)
                if (split[i] != "-")
                    Distances.Add(LevenshteinDistance(split[i], Input));
        }
        else
        {
            float count = 0;
            for (int i = 0; i < InSplit.Length; i++)
            {
                List<float> Distances2 = new List<float>();
                string[] split = SongName.Split(' ');
                for (int j = 0; j < split.Length; j++)
                    if (split[j] != "-")
                        Distances2.Add(LevenshteinDistance(split[j], InSplit[i]));
                count += Distances2.Min();
            }
            Distances.Add(count);
        }

        return Distances.Min();
    }
}
