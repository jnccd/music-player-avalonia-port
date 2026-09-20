using Android.Content;
using Android.OS;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPortMobile;

/// <summary>
/// Android specific plumbing of the mobile client: where the client keeps its own files and where the song
/// library is. Everything else (database, sync, choosing, voting, volume normalization) is shared with the
/// desktop client through MusicPlayerClientCore and does not know which platform it runs on.
/// </summary>
public static class MobilePlatform
{
    /// <summary>Folder name of the client's own data directory (see <see cref="PersistenceLocations"/>).</summary>
    public const string AppName = "MusicPlayerAvaloniaPortMobile";

    static readonly object initLock = new();
    static bool initialized;
    static Context? applicationContext;

    /// <summary>
    /// Points the shared core at this app's sandboxed data directory. Must run before the config or the
    /// song database is touched for the first time (the shared <c>Config</c>/<c>PersistenceLocations</c>
    /// resolve their paths lazily, exactly once) - the Android application and the activity both call it,
    /// only the first call does anything.
    /// </summary>
    public static void Initialize(Context context)
    {
        lock (initLock)
        {
            applicationContext ??= context.ApplicationContext ?? context;
            if (initialized)
                return;
            initialized = true;

            // On Android there is no "folder next to the executable" to persist into (the app package is
            // read only), so the client's own files live in the app's private files directory. Everything
            // the core persists (config, config backup, song.db, the download staging folder, the error
            // log) ends up there.
            string dataDirectory = applicationContext.FilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Android did not provide a files directory.");
            Directory.CreateDirectory(dataDirectory);

            PersistenceLocations.Configure(AppName, () => dataDirectory);
            MobileLog.Info($"Data directory: {dataDirectory}");

            // The crash dialog on Android only offers "send the summary to the OS developers", so an
            // unhandled exception is otherwise invisible to whoever has to fix it. Log it first.
            Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
                MobileLog.Error("Unhandled exception (AndroidEnvironment)", args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                MobileLog.Error("Unhandled exception (AppDomain)", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
                MobileLog.Error("Unobserved task exception", args.Exception);
        }
    }

    /// <summary>The application context (available after <see cref="Initialize"/>).</summary>
    public static Context? ApplicationContext => applicationContext;

    /// <summary>
    /// Makes sure the configured song library folder exists and actually contains songs. A configured
    /// folder wins as long as it is usable (the user may have pointed the client at an SD card folder);
    /// otherwise the platform's public music directory is used, which is where music on a phone lives.
    /// Returns the folder to scan or null when none could be found.
    /// </summary>
    public static string? ResolveSongLibraryPath()
    {
        string? configured = Config.Data.SongLibraryPath;
        MobileLog.Info($"Resolving the song library (configured: \"{configured ?? "<none>"}\")");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            string? rejected = DescribeLibraryFolderProblem(configured);
            if (rejected == null)
            {
                MobileLog.Info($"Using the configured song library \"{configured}\"");
                return configured;
            }
            MobileLog.Warn($"The configured song library \"{configured}\" is not usable: {rejected}");
        }

        string? discovered = FindDefaultMusicFolder();
        if (discovered != null)
        {
            Config.Data.SongLibraryPath = discovered;
            Config.Save();
            MobileLog.Info($"Discovered the song library \"{discovered}\"");
        }
        else
        {
            MobileLog.Warn("No usable song library folder was found (see the candidate list above).");
        }

        return discovered;
    }

    /// <summary>
    /// A human readable summary of where the client looked for music and why each place was accepted or
    /// rejected. Shown in the settings sheet, so a phone whose music lives somewhere unusual can be
    /// diagnosed without a log reader.
    /// </summary>
    public static string DescribeLibraryCandidates()
    {
        var description = new StringBuilder();
        string? configured = Config.Data.SongLibraryPath;
        description.AppendLine($"configured: {configured ?? "<none>"}");
        if (!string.IsNullOrWhiteSpace(configured))
            description.AppendLine($"  → {DescribeLibraryFolderProblem(configured) ?? "usable"}");

        foreach (string candidate in GetCandidateMusicFolders())
            description.AppendLine($"{candidate}: {DescribeLibraryFolderProblem(candidate) ?? "usable"}");

        if (description.Length == 0)
            description.Append("(no candidates)");

        return description.ToString();
    }

    /// <summary>
    /// Why the given folder cannot be used as the song library, or null when it can. Split out of
    /// <see cref="ResolveSongLibraryPath"/> so the reason is logged instead of only having the effect - on a
    /// phone the interesting failures are "does not exist" (wrong path) and "cannot be read" (storage
    /// permission / scoped storage), and they need different fixes.
    /// </summary>
    public static string? DescribeLibraryFolderProblem(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return "the folder does not exist";
            if (!HelperFuncs.DirOrSubDirsContainMp3(folder))
                return "the folder contains no mp3 files (or they are not readable)";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"access denied ({ex.Message})";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// The music folders this device offers, most likely first: the public music directory of the shared
    /// storage (Android's standard location, also what most desktop sync tools write into) and the same
    /// directory on a removable card when the device has one.
    /// </summary>
    public static IReadOnlyList<string> GetCandidateMusicFolders()
    {
        var folders = new List<string>();

        void Add(Java.IO.File? directory)
        {
            string? path = directory?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            MobileLog.Info($"Candidate music folder: \"{path}\" (exists: {Directory.Exists(path)})");
            if (!Directory.Exists(path) || folders.Contains(path))
                return;
            folders.Add(path);
        }

        try
        {
            Add(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic));
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not ask Android for the public music directory: {ex.Message}");
        }

        return folders;
    }

    /// <summary>The first candidate music folder that actually contains mp3 files, or null.</summary>
    static string? FindDefaultMusicFolder()
    {
        foreach (string folder in GetCandidateMusicFolders())
        {
            string? problem = DescribeLibraryFolderProblem(folder);
            if (problem == null)
                return folder;

            // The folder may be unreadable (permission not granted yet) or simply hold no music; either way
            // it is not the library, and the reason belongs in the log.
            MobileLog.Warn($"Rejecting candidate \"{folder}\": {problem}");
        }

        return null;
    }

    /// <summary>
    /// Opens a URL in the device's browser (used for the account registration page of the sync server).
    /// Returns false when Android refused the intent, e.g. because no browser is installed.
    /// </summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            var context = applicationContext ?? throw new InvalidOperationException("The mobile platform was not initialized.");
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open \"{url}\": {ex.Message}");
            return false;
        }
    }
}
