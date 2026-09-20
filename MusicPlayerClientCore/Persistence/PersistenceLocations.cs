using System;
using System.IO;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerSyncInterface;

namespace MusicPlayerAvaloniaPort.Persistence;

/// <summary>
/// Single source of truth for the client's persisted files. Everything lives in ONE data directory -
/// the config, its backup, the SQLite database and the staging folder for downloads - which is resolved
/// by <see cref="AppPaths"/>. On Linux that is $XDG_DATA_HOME/MusicPlayerAvaloniaPort-&lt;Debug|Release&gt;
/// (~/.local/share/... by default), everywhere else the "Persistence" folder next to the executable, and
/// all of it can be overridden with the MUSIC_PLAYER_DATA_DIR environment variable.
///
/// Platforms without an "executable adjacent" folder (Android: the app package is read only, and the
/// sandboxed app data directory is the only writable location the platform offers) configure their own
/// directory through <see cref="Configure"/> before anything touches the files - the mobile client does
/// that in its Android <c>Application</c>/<c>MainActivity</c> bootstrap.
///
/// Reading <see cref="DataDirectory"/> for the first time also migrates the files of an existing
/// installation (see <see cref="CopyFilesFromLegacyDirectory"/>), so a config and a song database from a
/// previous version are picked up instead of being silently left behind in the build output folder. That
/// migration is desktop-only: it only makes sense where <see cref="AppPaths"/> actually moved.
/// </summary>
public static class PersistenceLocations
{
    public const string DatabaseFileName = "song.db";
    public const string ConfigFileName = "config.json";
    public const string ConfigBackupFileName = "config_backup.json";
    public const string ErrorLogFileName = "error.log";
    public const string ExportLogFileName = "export.log";
    public const string TempDownloadFolderName = "tmpDownloads";

    /// <summary>Default folder name; the desktop client keeps it, the mobile client overrides it.</summary>
    public const string DefaultAppName = "MusicPlayerAvaloniaPort";

    static string appName = DefaultAppName;
    static Func<string>? dataDirectoryResolver;
    static string? resolvedDataDirectory;
    static readonly object resolveLock = new();

    /// <summary>The name the data directory is derived from (folder name on Linux / XDG root).</summary>
    public static string AppName => appName;

    /// <summary>
    /// Points the client at its own app name and (optionally) its own data directory. Meant to be called
    /// once during startup, before the config or the database is touched: the desktop client leaves the
    /// defaults alone or keeps them with a different app name, the mobile client passes its Android data
    /// directory.
    /// </summary>
    /// <param name="applicationName">Folder name of the app (see <see cref="AppPaths.GetFolderName"/>).</param>
    /// <param name="dataDirectory">Resolves the data directory verbatim; null keeps the platform default.</param>
    public static void Configure(string applicationName, Func<string>? dataDirectory = null)
    {
        lock (resolveLock)
        {
            appName = string.IsNullOrWhiteSpace(applicationName) ? DefaultAppName : applicationName;
            dataDirectoryResolver = dataDirectory;
            resolvedDataDirectory = null;
        }
    }

    /// <summary>The folder holding all of the client's persisted files (created on first access).</summary>
    public static string DataDirectory
    {
        get
        {
            var resolved = resolvedDataDirectory;
            if (resolved != null)
                return resolved;

            lock (resolveLock)
            {
                return resolvedDataDirectory ??= ResolveDataDirectory();
            }
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public static string ConfigPath => Path.Combine(DataDirectory, ConfigFileName);
    public static string ConfigBackupPath => Path.Combine(DataDirectory, ConfigBackupFileName);
    public static string ErrorLogPath => Path.Combine(DataDirectory, ErrorLogFileName);
    /// <summary>Diagnostics of the last library export (see <see cref="MusicPlayerAvaloniaPort.Helpers.Export.ExportLog"/>).</summary>
    public static string ExportLogPath => Path.Combine(DataDirectory, ExportLogFileName);
    public static string TempDownloadDirectory => Path.Combine(DataDirectory, TempDownloadFolderName);

    static string ResolveDataDirectory()
    {
        var resolver = dataDirectoryResolver;
        if (resolver != null)
        {
            string customDirectory = resolver();
            Directory.CreateDirectory(customDirectory);
            return customDirectory;
        }

        string dataDirectory = AppPaths.EnsureDataDirectory(appName, RunConfiguration);

        // Only Linux moved: off Linux the data directory IS the "Persistence" folder next to the executable
        // that older versions already used, so there is nothing to migrate there.
        if (!PathsEqual(AppPaths.GetExecutableAdjacentDataDirectory(), dataDirectory))
            CopyFilesFromLegacyDirectory(dataDirectory);

        return dataDirectory;
    }

    /// <summary>
    /// The build configuration suffix the data directory is separated by on Linux (see
    /// <see cref="AppPaths.GetDataDirectory"/>).
    /// </summary>
    static string RunConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>
    /// One-time migration of an existing installation. Before the data directory existed, the files were
    /// kept in the "Persistence" folder next to the executable - which on Linux is the build output folder -
    /// so whatever of them is not in the data directory yet is copied over. The old files are left
    /// untouched: the migration is repeatable, and nothing is lost if the data directory is deleted later.
    /// </summary>
    static void CopyFilesFromLegacyDirectory(string dataDirectory)
    {
        string legacyDirectory = AppPaths.GetExecutableAdjacentDataDirectory();

        string[] migratedFileNames =
        [
            ConfigFileName,
            ConfigBackupFileName,
            DatabaseFileName,
            $"{DatabaseFileName}-wal", // Write-ahead log: can hold commits that were never checkpointed
        ];

        foreach (string fileName in migratedFileNames)
        {
            string legacyFile = Path.Combine(legacyDirectory, fileName);
            string migratedFile = Path.Combine(dataDirectory, fileName);
            if (!File.Exists(legacyFile) || File.Exists(migratedFile))
                continue;

            try
            {
                File.Copy(legacyFile, migratedFile);
                Console.WriteLine($"Migrated \"{legacyFile}\" to \"{migratedFile}\".");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not migrate \"{legacyFile}\": {ex.Message}");
            }
        }
    }

    static bool PathsEqual(string firstPath, string secondPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar),
            comparison);
    }
}
