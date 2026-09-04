using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Helpers;

/// <summary>
/// Resolves the video id of a song title through yt-dlp (like the DxMGP port did). The executable is
/// found like this:
/// 1. A bundled copy next to the app ("yt-dlp.exe" on Windows / "yt-dlp" elsewhere) - that is the
///    Windows deployment path (ship the exe with the program, like youtube-dl.exe used to be shipped
///    with the DxMGP port).
/// 2. Otherwise the "yt-dlp" on PATH (the normal case on Linux, where it is a system package).
/// Every lookup runs "yt-dlp ytsearch:&lt;title&gt; --get-id" with a timeout and never throws.
/// </summary>
public static class YoutubeHelper
{
    const int SearchTimeLimitSeconds = 10;

    public static string? FindYtDlpExecutable()
    {
        // 1. Bundled copy next to the app (only checked when it actually exists).
        string bundledName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        string bundledPath = Path.Combine(AppContext.BaseDirectory, bundledName);
        if (File.Exists(bundledPath))
            return bundledPath;

        // 2. yt-dlp on PATH. Returning the bare name lets Process.Start resolve it through PATH.
        string pathName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        if (IsOnPath(pathName))
            return pathName;

        return null;
    }

    static bool IsOnPath(string fileName)
    {
        try
        {
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathVariable))
                return false;

            string separator = OperatingSystem.IsWindows() ? ';'.ToString() : ":";
            foreach (string directory in pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, fileName)))
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Looks up the video id for the given song title through a yt-dlp title search. Returns null when
    /// yt-dlp is unavailable, the search times out or fails. Never throws.
    /// </summary>
    public static async Task<string?> GetYoutubeVideoIdAsync(string songTitle)
    {
        string? ytDlpExecutable = FindYtDlpExecutable();
        if (ytDlpExecutable == null || string.IsNullOrWhiteSpace(songTitle))
            return null;

        try
        {
            var startInfo = new ProcessStartInfo(ytDlpExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add($"ytsearch:{songTitle}");
            startInfo.ArgumentList.Add("--get-id");
            startInfo.ArgumentList.Add("--skip-download");
            startInfo.ArgumentList.Add("--no-playlist");

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(SearchTimeLimitSeconds));
            try
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string output = await stdoutTask.ConfigureAwait(false);

                return output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                    ?.Trim();
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    public static string BuildWatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";
}
