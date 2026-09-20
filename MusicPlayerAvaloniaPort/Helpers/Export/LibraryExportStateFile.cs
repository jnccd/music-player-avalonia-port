using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayerAvaloniaPort.Services.Infrastructure;

namespace MusicPlayerAvaloniaPort.Helpers.Export;

/// <summary>
/// Carries the song library's state file (<c>.song-library.music-player-config</c>) into an export.
///
/// The file records which account a library belongs to and the number of the last song library migration
/// applied to it (see the sync server's song-library-migrations doc, §3.3). An exported library is a copy of
/// the current library - the file names in it are already up to date - so copying the file along makes the
/// export say exactly that: "this library belongs to this account and is at migration N".
///
/// Without it the export is an unowned folder of files: a client pointed at it assumes "fully migrated" (so
/// nothing is renamed retroactively, which is safe) and silently CLAIMS it for the account that pulls next
/// (§3.3/§5.4), while an export that carries the file either matches its account straight away or gets the
/// intended "this library belongs to another account" take-over prompt.
/// </summary>
public static class LibraryExportStateFile
{
    /// <summary>
    /// The state file of the configured song library, or null when that library has none yet (nothing is
    /// recorded, so there is nothing to carry over - the export behaves like a library without a state file,
    /// which is a supported case).
    /// </summary>
    public static string? ResolveSourceFile(string? songLibraryPath)
    {
        if (string.IsNullOrWhiteSpace(songLibraryPath))
            return null;

        try
        {
            string stateFile = SongSyncService.GetSongLibraryConfigFilePath(songLibraryPath);
            return File.Exists(stateFile) ? stateFile : null;
        }
        catch (Exception ex)
        {
            ExportLog.Write($"could not look for the library state file in \"{songLibraryPath}\": {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copies the library's state file into the export through the run's own copier, so it works for a normal
    /// folder and for a device alike, and returns a line for the export summary / diagnostics log. An export
    /// that already holds the file is left as it is (the copier skips existing files): the recorded number
    /// may then be older than the library's, which only means a client using the export would apply the
    /// migrations in between - and those simply find no file of the old name in a copy that is already
    /// renamed, so nothing happens.
    /// </summary>
    public static async Task<string> CopyIntoExportAsync(LibraryExportCopier.CopyFile copyFile, string? songLibraryPath)
    {
        string? stateFile = ResolveSourceFile(songLibraryPath);
        if (stateFile == null)
        {
            ExportLog.Write("library state file: the song library has no .song-library.music-player-config, so none was exported");
            return "The song library has no state file yet, so none was exported "
                + "(the export is treated as an up-to-date library by the first client that uses it).";
        }

        ExportCopyStep step;
        try
        {
            step = await copyFile(stateFile, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ExportLog.Write($"library state file: copying it failed: {ex.Message}");
            return $"The library state file could not be written into the export: {ex.Message}";
        }

        string outcome = step.Outcome switch
        {
            ExportCopyOutcome.Copied => "The library state file (.song-library.music-player-config) was written into the export.",
            ExportCopyOutcome.SkippedAlreadyThere => "The export already holds a library state file, it was left as it is.",
            _ => $"The library state file could not be written into the export: {step.Error}"
        };

        ExportLog.Write($"library state file: {outcome}");
        return outcome;
    }
}
