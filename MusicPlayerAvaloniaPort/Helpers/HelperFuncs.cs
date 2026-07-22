using Avalonia.Media;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Helpers;

public static class HelperFuncs
{
    /// <summary>
    /// Recursively finds all mp3 files in a directory and its subdirectories.
    /// </summary>
    /// <param name="StartDir">The directory to search in.</param>
    /// <returns>Absolute paths of all mp3 files</returns>
    public static List<string> FindAllMp3FilesInDir(string StartDir)
    {
        List<string> re = new();
        foreach (string s in Directory.GetFiles(StartDir))
            if (s.EndsWith(".mp3"))
            {
                re.Add(s);
            }

        foreach (string D in Directory.GetDirectories(StartDir))
            re.AddRange(FindAllMp3FilesInDir(D));

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

    public static (string Album, string Artists) GetAlbumAndArtistsFromSong(string songPath)
    {
        TagLib.File file = TagLib.File.Create(songPath);
        return (file.Tag.Album, file.Tag.AlbumArtists.Length == 0 ? "" : file.Tag.AlbumArtists.Aggregate((x, y) => x + " + " + y));
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

    public static Stream ModifyRGBChannelsAndKeepAlpha(SKBitmap bitmap, Color col)
    {
        // Lock the pixels
        IntPtr pixels = bitmap.GetPixels(out var pixelInfo);
        int width = bitmap.Width;
        int height = bitmap.Height;

        unsafe
        {
            // Cast the pixel buffer to a byte pointer
            byte* pixelPtr = (byte*)pixels.ToPointer();

            // Iterate over each pixel row
            for (int y = 0; y < height; y++)
            {
                // Get the start of the row
                byte* rowPtr = pixelPtr + (y * bitmap.RowBytes);

                // Iterate over each pixel in the row
                for (int x = 0; x < width; x++)
                {
                    // Calculate the offset for the current pixel (assuming 32-bit RGBA)
                    byte* pixel = rowPtr + (x * 4);

                    // Access and modify the pixel channels (RGBA order)
                    // byte r = pixel[2];
                    // byte g = pixel[1];
                    // byte b = pixel[0];
                    byte a = pixel[3];

                    // Example: Apply a red tint
                    pixel[2] = col.R;
                    pixel[1] = col.G;
                    pixel[0] = col.B;
                    pixel[3] = a;
                }
            }
        }

        // Save the modified image
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.AsStream();
    }

    static int RunAsConsoleCommandThreadIndex = 0;
    public static void RunAsConsoleCommand(this string command, int TimeLimitInSeconds, Action TimeoutEvent, Action<string, string> ExecutedEvent,
            Action<StreamWriter>? RunEvent = null)
    {
        bool exited = false;
        string[] split = command.Split(' ');

        if (split.Length == 0)
            return;

        Process compiler = new Process();
        compiler.StartInfo.FileName = split.First();
        compiler.StartInfo.Arguments = split.Skip(1).Aggregate("", (x, y) => x + " " + y);
        compiler.StartInfo.CreateNoWindow = true;
        compiler.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
        compiler.StartInfo.UseShellExecute = false;
        compiler.StartInfo.RedirectStandardInput = true;
        compiler.StartInfo.RedirectStandardOutput = true;
        compiler.StartInfo.RedirectStandardError = true;
        compiler.Start();

        Task.Factory.StartNew(() => { RunEvent?.Invoke(compiler.StandardInput); });

        DateTime start = DateTime.Now;

        Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Name = $"RunAsConsoleCommand Thread {RunAsConsoleCommandThreadIndex++}";
            compiler.WaitForExit();

            string o = "";
            string e = "";

            try { o = compiler.StandardOutput.ReadToEnd(); } catch { }
            try { e = compiler.StandardError.ReadToEnd(); } catch { }

            ExecutedEvent(o, e);
            exited = true;
        });

        while (!exited && (DateTime.Now - start).TotalSeconds < TimeLimitInSeconds)
            Thread.Sleep(100);
        if (!exited)
        {
            exited = true;
            try
            {
                compiler.Close();
            }
            catch { }
            TimeoutEvent();
        }
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