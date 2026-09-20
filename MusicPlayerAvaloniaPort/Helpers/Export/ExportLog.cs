using System;
using System.IO;
using MusicPlayerAvaloniaPort.Persistence;

namespace MusicPlayerAvaloniaPort.Helpers.Export;

/// <summary>
/// Diagnostics of the library export, written to a file in the app's data directory (see
/// <see cref="PersistenceLocations.ExportLogPath"/>) and shown by the "Diagnostics" button of the export
/// window.
///
/// The export to a device needs this: the shell performs the copy asynchronously and returns nothing at all
/// when it does not work, so "the copy did not finish in time" is as much as the app can say on its own. The
/// log records what the shell was asked to do (folder identity, flags, thread/apartment) and what came back
/// (item visible? size? item count?), which is what tells a device problem from an app problem.
///
/// Never throws: logging must not be able to break an export.
/// </summary>
public static class ExportLog
{
    /// <summary>Stop appending after this much text, so a run over a big library cannot fill the disk.</summary>
    const long MaxLogBytes = 1024 * 1024;

    static readonly object lockject = new();
    static bool capped;

    public static string Path => PersistenceLocations.ExportLogPath;

    /// <summary>Starts the log of a new export run (the log of the previous run is replaced).</summary>
    public static void StartRun(string header)
    {
        lock (lockject)
        {
            capped = false;
            try
            {
                Directory.CreateDirectory(PersistenceLocations.DataDirectory);
                File.WriteAllText(Path, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {header} ===\n");
            }
            catch
            {
                // No log then - the export itself must still work.
            }
        }
    }

    public static void Write(string message)
    {
        lock (lockject)
        {
            if (capped)
                return;

            try
            {
                Directory.CreateDirectory(PersistenceLocations.DataDirectory);
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
                if (new FileInfo(Path).Length > MaxLogBytes)
                {
                    capped = true;
                    File.AppendAllText(Path, "=== log capped ===\n");
                }
            }
            catch
            {
                // Ignore: diagnostics are best effort.
            }
        }
    }

    /// <summary>The end of the log, for the diagnostics dialog (the interesting part is the last run).</summary>
    public static string ReadTail(int maxCharacters = 40000)
    {
        lock (lockject)
        {
            try
            {
                if (!File.Exists(Path))
                    return "";

                string content = File.ReadAllText(Path);
                return content.Length <= maxCharacters ? content : content[^maxCharacters..];
            }
            catch (Exception ex)
            {
                return $"The export log could not be read: {ex.Message}";
            }
        }
    }
}
