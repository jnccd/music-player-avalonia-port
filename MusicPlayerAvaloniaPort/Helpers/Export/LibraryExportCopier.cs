using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Helpers.Export;

public enum ExportCopyOutcome
{
    Copied,
    /// <summary>The destination already holds a file of that name, so it was left alone - an export only
    /// adds the songs that are not on the device yet.</summary>
    SkippedAlreadyThere,
    Failed
}

/// <summary>Result of copying one song (see <see cref="LibraryExportCopier.CopyFile"/>).</summary>
public readonly record struct ExportCopyStep(ExportCopyOutcome Outcome, string? Error = null);

public readonly record struct ExportCopyProgress(
    int Processed,
    int Total,
    string CurrentFileName,
    int Copied,
    int Skipped,
    int Failed,
    string? LastError);

public sealed record ExportCopyResult(int Copied, int Skipped, int Failed, long CopiedBytes, bool Cancelled, bool Aborted, IReadOnlyList<string> Errors);

/// <summary>
/// Copies the songs of an export into the destination folder. Existing files are left alone, a single
/// unreadable file does not abort the run, and the run can be cancelled between two files.
///
/// The copy of one song is a delegate (<see cref="CopyFile"/>) because the destinations differ: a normal
/// folder is copied with File.Copy on a thread pool thread (<see cref="CreateFileSystemCopier"/>), while a
/// phone connected over USB has to be copied through the shell from the app's UI thread
/// (<see cref="WindowsShellFolder.CreateCopier"/>). The run itself is asynchronous on purpose: the shell
/// needs its caller's message loop to keep running while it copies.
/// </summary>
public static class LibraryExportCopier
{
    /// <summary>
    /// Copies one song. The run awaits it per song, so an implementation may be asynchronous and decides on
    /// which thread its work happens.
    /// </summary>
    public delegate Task<ExportCopyStep> CopyFile(string sourceFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// How many songs in a row may fail before the run gives up. A destination that does not accept the
    /// files (device gone, folder not writable) fails every song, and each of those failures can cost the
    /// full copy timeout - without this limit a run into a dead device would sit there for hours.
    /// </summary>
    public const int MaxConsecutiveFailures = 3;

    public static async Task<ExportCopyResult> CopyAsync(
        IReadOnlyList<ExportCandidate> songs,
        CopyFile copyFile,
        IProgress<ExportCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        int copied = 0, skipped = 0, failed = 0;
        long copiedBytes = 0;
        bool cancelled = false;
        bool aborted = false;
        int consecutiveFailures = 0;
        string? lastError = null;
        List<string> errorMessages = [];
        Dictionary<string, int> errorCounts = [];

        ExportLog.Write($"run: {songs.Count} song(s), {ExportCandidate.FormatSize(songs.Sum(song => song.SizeBytes ?? 0))}, ({DescribeThread()})");

        for (int i = 0; i < songs.Count; i++)
        {
            // A single File.Copy cannot be interrupted, so cancelling stops the run between two files.
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            ExportCandidate song = songs[i];

            ExportCopyStep step;
            try
            {
                step = await copyFile(song.FilePath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }

            switch (step.Outcome)
            {
                case ExportCopyOutcome.Copied:
                    copied++;
                    copiedBytes += song.SizeBytes ?? 0;
                    consecutiveFailures = 0;
                    break;
                case ExportCopyOutcome.SkippedAlreadyThere:
                    skipped++;
                    consecutiveFailures = 0;
                    break;
                default:
                    failed++;
                    consecutiveFailures++;
                    // A destination that cannot be written to at all fails every single song with the same
                    // message (a device that does not accept the files reported it once per song): count the
                    // repeats instead of repeating the text, so the summary stays readable.
                    string message = step.Error ?? $"{song.FileName}: copying failed.";
                    lastError = message;
                    if (errorCounts.TryGetValue(message, out int occurrences))
                        errorCounts[message] = occurrences + 1;
                    else
                    {
                        errorCounts[message] = 1;
                        errorMessages.Add(message);
                    }
                    break;
            }

            progress?.Report(new ExportCopyProgress(i + 1, songs.Count, song.FileName, copied, skipped, failed, lastError));

            if (consecutiveFailures >= MaxConsecutiveFailures)
            {
                aborted = true;
                ExportLog.Write($"run aborted: {consecutiveFailures} failures in a row (last: {lastError})");
                break;
            }
        }

        ExportLog.Write($"run finished: copied={copied}, skipped={skipped}, failed={failed}, cancelled={cancelled}, aborted={aborted}");

        return new ExportCopyResult(copied, skipped, failed, copiedBytes, cancelled, aborted,
            [.. errorMessages.Select(message => errorCounts[message] > 1 ? $"{message} (x{errorCounts[message]})" : message)]);
    }

    /// <summary>Which thread/apartment the run happens on (device copies only work on an STA with a pump).</summary>
    static string DescribeThread() =>
        OperatingSystem.IsWindows()
            ? $"thread {Environment.CurrentManagedThreadId}, apartment {Thread.CurrentThread.GetApartmentState()}"
            : $"thread {Environment.CurrentManagedThreadId}";

    /// <summary>
    /// The copier for a normal folder - a removable drive, an SD card, or a phone mounted like a filesystem
    /// (on Linux an MTP phone shows up under /run/user/&lt;id&gt;/gvfs/). Every song is copied on a thread
    /// pool thread, so a slow (network) library never blocks the UI thread that drives the run. Existence is
    /// checked before the copy, so an export into a folder that already holds the library (or into the
    /// library itself) leaves those files untouched instead of failing on "source and destination are the
    /// same file".
    /// </summary>
    public static CopyFile CreateFileSystemCopier(string destinationFolder)
    {
        return (string sourceFilePath, CancellationToken cancellationToken) => Task.Run(() =>
        {
            string targetPath = Path.Combine(destinationFolder, Path.GetFileName(sourceFilePath));
            try
            {
                if (File.Exists(targetPath))
                    return new ExportCopyStep(ExportCopyOutcome.SkippedAlreadyThere);

                File.Copy(sourceFilePath, targetPath, overwrite: false);
                return new ExportCopyStep(ExportCopyOutcome.Copied);
            }
            catch (Exception ex)
            {
                return new ExportCopyStep(ExportCopyOutcome.Failed, $"{Path.GetFileName(sourceFilePath)}: {ex.Message}");
            }
        }, cancellationToken);
    }
}
