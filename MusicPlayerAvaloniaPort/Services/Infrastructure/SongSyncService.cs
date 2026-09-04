using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EzAuth.Interfaces;
using EzAuth.Keycloak;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerSyncInterface;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncInterface.DTOs.Composites;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongSyncService))]
public class SongSyncService
{
    readonly HttpClient HttpClient;
    readonly IEzAuth AuthBackend;
    readonly DbWrapperService DbWrapper;

    private IEzAuthHttpClient? client = null;
    EzAuthAddress? authBackendAddress = null;
    public string State { get => state; private set { OnStateChanged?.Invoke(value); state = value; } }
    private string state = "";
    public Action<string>? OnStateChanged = null;
    /// <summary>
    /// Coarse progress (0..1) of the ongoing sync pull, reported at its milestones: 0 while the pull
    /// request is still in flight (a network request has no measurable progress until the server
    /// answers), then rising as the payload arrives, the local database rewrite runs and the song
    /// library migrations are applied. Always reaches 1 before <see cref="Pull"/> returns - on success,
    /// on the account-mismatch abort and on failure alike - so a startup sequence can hand the progress
    /// bar over to the next stage once the pull finished.
    /// </summary>
    public float SyncProgress { get; private set; } = 0;
    readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    const string ROUTE_VERSION_PREFIX = "/v1";
    const string SONG_LIBRARY_CONFIG_FILE_NAME = ".song-library.music-player-config";

    /// <summary>
    /// The song library migrations that came with the last successful pull. Used to apply pending
    /// migrations (e.g. song file renames) to the local song library.
    /// </summary>
    public SongLibraryMigration[] LastPulledMigrations { get; private set; } = [];

    /// <summary>
    /// The user id of the account the last successful pull was made for. The song library config file
    /// records which account a song library belongs to, so migrations are only applied to a library
    /// when its recorded account matches the account that is pulling.
    /// </summary>
    public string? LastPulledUserId { get; private set; } = null;

    string? songLibraryOwnerWarning = null;

    // Guards the background tag/upload worker so only one runs at a time (a scan and a previous worker
    // must not process the same pending songs twice concurrently).
    readonly object pendingUploadWorkerLock = new();
    bool pendingUploadWorkerRunning = false;
    // Reads the tags of lazily registered songs from the (slow, e.g. NAS) library with this concurrency.
    const int PENDING_UPLOAD_WORKER_DEGREE_OF_PARALLELISM = 4;

    /// <summary>
    /// Returns the last recorded song library account warning (see <see cref="WriteSongLibraryMigrationState"/>) and clears it.
    /// UI code should show it to the user (e.g. a MessageBox) so misconfigured libraries are noticed.
    /// </summary>
    public string? TakeSongLibraryOwnerWarning()
    {
        string? warning = songLibraryOwnerWarning;
        songLibraryOwnerWarning = null;
        return warning;
    }

    public SongSyncService(HttpClient HttpClient, IEzAuth AuthBackend, DbWrapperService DbWrapper)
    {
        this.HttpClient = HttpClient;
        this.AuthBackend = AuthBackend;
        this.DbWrapper = DbWrapper;

        Init();
    }

    public void Init(string? password = null, bool TryCallApiInit = false, bool RetryUnsyncedEntries = true)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/init";
        try
        {
            authBackendAddress = GetAuthBackendAddress(Config.Data.SyncServerHost
                ?? throw new Exception($"{nameof(Config.Data.SyncServerHost)} is null!"))
                ?? throw new Exception($"{nameof(GetAuthBackendAddress)} returned null!");
            client = new KeyCloakHttpClient(authBackendAddress, authBackendRefreshToken =>
            {
                Config.Data.AuthBackendRefreshToken = authBackendRefreshToken;
                Config.Save();
            }, Config.Data.AuthBackendRefreshToken, HttpClient);

            if (password != null)
                client.Login(Config.Data.SyncServerUsername ?? throw new Exception($"{nameof(Config.Data.SyncServerUsername)} is null!"), password);
        }
        catch (Exception ex)
        {
            State = $"SyncManager Init failed: {ex.Message}";
            return;
        }

        // Init
        try
        {
            if (TryCallApiInit)
            {
                using var dbContext = DbWrapper.GetContext();
                var initRequest = dbContext.GetSyncInitRequest();
                var sendObjString = JsonSerializer.Serialize(initRequest, jsonOptions);
                var sendContent = new StringContent(sendObjString, Encoding.UTF8, "application/json");
                var res = client.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", sendContent).Result;
                State = $"Init {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
            }
        }
        catch (Exception ex)
        {
            State = $"API Init failed: {ex.Message}";
            return;
        }

        // Retry unsynced entries
        if (RetryUnsyncedEntries)
        {
            using var dbContext = DbWrapper.GetContext();
            var first20UnsyncedReqs = dbContext.GetNotYetSyncedDataEntries().Take(20);
            foreach (var unsyncedData in first20UnsyncedReqs)
            {
                try
                {
                    if (unsyncedData.Endpoint == "/sync/new-song")
                    {
                        // Lazy registrations (see DbWrapperService.AddNewUpvotedSongLazy) are only
                        // uploaded after their tags were read and persisted by the background worker.
                        UpvotedSong? currentRow = unsyncedData.BelongedToSongId != null
                            ? dbContext.GetUpvotedSongByIdOrNull(unsyncedData.BelongedToSongId)
                            : null;
                        if (unsyncedData.Error == DbWrapperService.PendingTagReadError)
                        {
                            if (currentRow == null)
                            {
                                // The row was removed (e.g. by an earlier pull): the marker is
                                // meaningless - the next library scan re-registers the song if needed.
                                dbContext.RemoveNotYetSyncedDataEntries(unsyncedData);
                                continue;
                            }
                            if (SongFileMatching.HasNoAlbumOrArtist(currentRow.Artist, currentRow.Album))
                                continue; // Tags not read yet - the background worker owns this marker
                        }

                        // Refresh the queued body from the current row (its tags may have been filled in
                        // the meantime, it may have been renamed): a stale body could otherwise recreate
                        // the row under an old state on the server.
                        if (currentRow != null && currentRow.Name.Length > 0)
                            unsyncedData.Body = JsonSerializer.Serialize(currentRow, jsonOptions);
                    }

                    var sendContent = new StringContent(unsyncedData.Body, Encoding.UTF8, "application/json");
                    HttpResponseMessage res;
                    if (unsyncedData.Endpoint == "/sync/volume") // Horrible way to do this, TODO: change unsyncedData to include HTTP method type (POST/PUT) instead of hardcoding it here
                        res = client.PutAsync($"{Config.Data.SyncServerHost}{ROUTE_VERSION_PREFIX}{unsyncedData.Endpoint}", sendContent).Result;
                    else
                        res = client.PostAsync($"{Config.Data.SyncServerHost}{ROUTE_VERSION_PREFIX}{unsyncedData.Endpoint}", sendContent).Result;

                    Console.WriteLine($"Synced data for endpoint {unsyncedData.Endpoint}: {res.StatusCode}, {unsyncedData.Body}");

                    if (res.StatusCode == System.Net.HttpStatusCode.Conflict && unsyncedData.Endpoint == "/sync/new-song")
                    {
                        // The server rejected the song upload because the same song (same file name and
                        // album/artist tags) already exists under another SongId - e.g. another client of
                        // this account registered the same file, or this upload went through before while
                        // the response was lost. The response body is the existing (canonical) row.
                        // Redirect everything this client still has queued under its own SongId of that
                        // song (votes etc.) to the canonical row and drop the upload itself.
                        try
                        {
                            var queuedSong = JsonSerializer.Deserialize<UpvotedSong>(unsyncedData.Body, jsonOptions);
                            var canonicalSong = JsonSerializer.Deserialize<UpvotedSong>(res.Content.ReadAsStringAsync().Result, jsonOptions);
                            if (queuedSong?.SongId != Guid.Empty && canonicalSong?.SongId != Guid.Empty && queuedSong!.SongId != canonicalSong!.SongId)
                            {
                                int redirected = dbContext.RedirectQueuedEntriesToSong(queuedSong!.SongId, canonicalSong!.SongId);
                                Console.WriteLine($"Song \"{queuedSong.Name}\" already exists on the server as {canonicalSong.SongId}, redirected {redirected} queued entr(y/ies) to it.");
                            }
                            else
                            {
                                Console.WriteLine($"Song upload was rejected as duplicate, but no canonical row could be read from the response - dropping the queued upload.");
                            }
                        }
                        catch (Exception ex)
                        {
                            // E.g. an older server version that rejects duplicates without returning the
                            // existing row: nothing can be redirected, just drop the upload like before.
                            Console.WriteLine($"Could not read the canonical row of a rejected song upload: {ex.Message}");
                        }
                        dbContext.RemoveNotYetSyncedDataEntries(unsyncedData);
                    }
                    else if (res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        dbContext.RemoveNotYetSyncedDataEntries(unsyncedData);
                    }
                    else if (res.StatusCode == System.Net.HttpStatusCode.NotFound && unsyncedData.Endpoint != "/sync/new-song")
                    {
                        // The server does not know the song this request refers to: its upload was merged
                        // away or the row was deleted. Retrying can never succeed. If an upload of the same
                        // song is still queued (behind this entry), leave it for the upload's conflict
                        // handling to redirect; otherwise drop it so it is not retried on every startup.
                        bool uploadStillQueued = unsyncedData.BelongedToSongId != null && dbContext
                            .GetNotYetSyncedDataEntries()
                            .Any(x => x.Endpoint == "/sync/new-song" && x.BelongedToSongId == unsyncedData.BelongedToSongId);
                        if (!uploadStillQueued)
                        {
                            Console.WriteLine($"Server does not know the song of queued {unsyncedData.Endpoint} data (anymore), dropping it: {unsyncedData.Body}");
                            dbContext.RemoveNotYetSyncedDataEntries(unsyncedData);
                        }
                    }
                }
                catch (Exception ex)
                {
                    State = $"API Retry Unsynced Entries failed: {ex.Message}";
                    Console.WriteLine($"API Retry Unsynced Entries failed for endpoint {unsyncedData.Endpoint}: {ex.Message}, {unsyncedData.Body}");
                }
            }
        }
    }

    public EzAuthAddress? GetAuthBackendAddress(string? syncServerHost)
    {
        if (syncServerHost == null) return null;
        var res = HttpClient.GetAsync($"{syncServerHost}{ROUTE_VERSION_PREFIX}/authBackend").Result;
        var content = res.Content.ReadAsStringAsync().Result;
        var address = JsonSerializer.Deserialize<EzAuthAddress>(content, jsonOptions);
        return address;
    }

    public string GetAccountRegistrationAddress(string? syncServerHost = null) =>
        AuthBackend.GetAccountRegistrationAddress(
            GetAuthBackendAddress(syncServerHost)?.RealmUrl
            ?? throw new Exception("Got null from GetAuthBackendAddress!"));

    /// <summary>
    /// Pulls the latest data from the sync server and writes it into the local database.
    /// Before anything is written, the configured song library is checked: if it is registered for a
    /// different account than the one the pull is made for, the pull is aborted (nothing is synced, the
    /// database and the library state file stay untouched) and a warning is recorded, which the UI should
    /// surface to the user. The pull can then be retried with AdoptSongLibraryOnMismatch = true once the
    /// user explicitly agreed to take the library over for the current account.
    /// </summary>
    public void Pull(bool AdoptSongLibraryOnMismatch = false)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/pull";
        SyncProgress = 0; // A previous pull (e.g. a login pull) may have left it at the end state.
        try
        {
            var res = client!.GetStringAsync($"{Config.Data.SyncServerHost}{endpoint}").Result;
            var pulledData = JsonSerializer.Deserialize<SyncPullResponse>(res, jsonOptions);

            if (pulledData == null)
                throw new Exception("Pulled data was null!");
            if (pulledData.Songs.Count() == 0 || pulledData.HistoryEntries.Count() == 0)
                throw new Exception("Pulled data was empty!");

            // The payload arrived. From here on the remaining pull work (local database rewrite,
            // duplicate merge, song library migrations) is measurable, so start reporting progress.
            SyncProgress = 0.4f;

            string authedUserId = pulledData.User?.UserId ?? "";

            // Account check BEFORE any local side effects: if the configured song library is registered for
            // another account, stop here so the other accounts data is not written over the local database
            // and the library state is not touched before the user had a chance to decide what to do.
            bool libraryHasOtherOwner = false;
            if (authedUserId != "" && !string.IsNullOrWhiteSpace(Config.Data.SongLibraryPath) && Directory.Exists(Config.Data.SongLibraryPath))
            {
                if (TryReadSongLibraryMigrationState(Config.Data.SongLibraryPath, out string fileOwner, out _) && fileOwner != "" && fileOwner != authedUserId)
                {
                    libraryHasOtherOwner = true;
                    if (!AdoptSongLibraryOnMismatch)
                    {
                        songLibraryOwnerWarning =
                            $"The song library \"{Config.Data.SongLibraryPath}\" is registered for the account \"{fileOwner}\", but you are logged in as \"{authedUserId}\".\n\n" +
                            "Nothing was synced.\n" +
                            "You can log in with the account that owns this library, point this client at another song library, " +
                            "or take the library over for your account (its migration history will be dropped).";
                        Console.WriteLine(songLibraryOwnerWarning);
                        State = "Pull aborted: the song library belongs to another account, nothing was synced.";
                        return;
                    }
                }
            }

            Console.WriteLine($"Pulled {pulledData.Songs.Count()} songs and {pulledData.HistoryEntries.Count()} history entries, writing into local db...");

            LastPulledMigrations = pulledData.Migrations ?? [];
            LastPulledUserId = authedUserId != "" ? authedUserId : pulledData.User?.UserId;

            // The rewrite (and especially the duplicate-entry merge that follows it, which can remove a
            // few hundred rows when the server data used to contain duplicates) can take a moment - the
            // options view mirrors State, so say what is happening instead of appearing frozen.
            State = "Merging duplicate song entries after the pull…";
            int mergedDuplicates;
            using (var dbContext = DbWrapper.GetContext())
            {
                mergedDuplicates = dbContext.RewriteDatabase(pulledData, rewriteProgress =>
                {
                    // The rewrite reports its coarse write milestones, mapped onto the middle section
                    // of the sync stage (the duplicate merge after it is not covered by the callback).
                    SyncProgress = 0.4f + 0.4f * rewriteProgress;
                });
            }
            SyncProgress = 0.9f; // Database rewrite and duplicate merge done

            // If the user explicitly agreed to take the library over, register it for the current account
            // now (treated as fully migrated for it). Migrations are then applied as usual below, which is
            // a no-op, since the library state was just set to the latest known migration.
            if (libraryHasOtherOwner && AdoptSongLibraryOnMismatch && !string.IsNullOrWhiteSpace(Config.Data.SongLibraryPath))
            {
                int latestKnownNumber = LastPulledMigrations.Length > 0 ? LastPulledMigrations.Max(m => m.MigrationNumber) : 0;
                WriteSongLibraryMigrationState(Config.Data.SongLibraryPath, authedUserId, latestKnownNumber, recordMismatchWarning: false);
                Console.WriteLine($"Song library \"{Config.Data.SongLibraryPath}\" was taken over for account {authedUserId} (treated as fully migrated).");
            }

            // Song library migrations are synced with the pull: apply pending ones (e.g. file renames) to
            // the local song library. The library has to be known for that, otherwise this is done later
            // once the user sets the library folder.
            if (Config.Data.SongLibraryPath != null)
                ApplySongLibraryMigrations(Config.Data.SongLibraryPath);

            State = mergedDuplicates > 0
                ? $"Pull Succeeded! (merged {mergedDuplicates} duplicate song entr{(mergedDuplicates == 1 ? "y" : "ies")})"
                : "Pull Succeeded!";
        }
        catch (Exception ex)
        {
            State = $"Pull failed: {ex.Message}";
        }
        finally
        {
            // Every exit path (success, account-mismatch abort, failure) means the sync stage is done:
            // the caller hands the progress bar over to the next startup stage once Pull() returns.
            SyncProgress = 1;
        }
    }

    /// <summary>
    /// Tries to create a song library migration on the server. The server assigns the migration number
    /// and renames the matching UpvotedSong rows, so this POST is the commit point of a rename: only
    /// if it succeeds should the client rename the actual file in its song library.
    /// Returns the created migration (including its assigned MigrationNumber) or null if the POST failed.
    /// </summary>
    public SongLibraryMigration? PostSongLibraryMigration(SongLibraryMigration migration)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/song-library-migration";
        try
        {
            var migrationJson = JsonSerializer.Serialize(migration, jsonOptions);
            var migrationContent = new StringContent(migrationJson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", migrationContent).Result;

            State = $"PostSongLibraryMigration {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
            if (!res.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<SongLibraryMigration>(res.Content.ReadAsStringAsync().Result, jsonOptions);
        }
        catch (Exception ex)
        {
            State = $"PostSongLibraryMigration failed: {ex.Message}";
            return null;
        }
    }

    public static string GetSongLibraryConfigFilePath(string libraryPath) => Path.Combine(libraryPath, SONG_LIBRARY_CONFIG_FILE_NAME);

    /// <summary>
    /// Reads the song library config file. Returns false when the file does not exist or cannot be parsed.
    /// The file has two lines: the user id of the account the song library belongs to, then the number of
    /// the last applied song library migration. Files from before the account check existed only contain
    /// the number (one line); those come back with an empty ownerUserId.
    /// </summary>
    static bool TryReadSongLibraryMigrationState(string libraryPath, out string ownerUserId, out int state)
    {
        ownerUserId = "";
        state = 0;
        try
        {
            string configFilePath = GetSongLibraryConfigFilePath(libraryPath);
            if (!File.Exists(configFilePath))
                return false;

            string[] lines = File.ReadAllText(configFilePath).Replace("\r", "").Split('\n');
            if (lines.Length >= 2 && int.TryParse(lines[1].Trim(), out state))
            {
                ownerUserId = lines[0].Trim(); // New format: account user id + migration number
                return true;
            }
            if (lines.Length >= 1 && int.TryParse(lines[0].Trim(), out state))
                return true; // Legacy format: just the migration number

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TryReadSongLibraryMigrationState failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Writes the migration state of the given song library for the given account: the account user id the
    /// song library belongs to, and the number of the last applied song library migration.
    /// The config file lives in the song library folder, so multiple clients sharing the same library
    /// (e.g. via a NAS) share the file as well, which is why the recorded account matters:
    /// - No (or unparseable) config file: it is created with the given account and number.
    /// - Legacy file (number only, from before the account check existed): adopted for the given account,
    ///   keeping the higher of the two numbers, so no recorded migration is lost.
    /// - File of a different account: unless recordMismatchWarning is false (explicit user consent, see
    ///   <see cref="AdoptSongLibrary"/>), a warning is recorded (see <see cref="TakeSongLibraryOwnerWarning"/>)
    ///   and the file is re-initialized for the current account, treating the library as up to date for it.
    /// - File of the same account: the number is only ever moved forward - if the file already contains a
    ///   higher (or equal) number, another client (e.g. sharing the library via NAS) was faster and the
    ///   file is left untouched.
    /// </summary>
    public void WriteSongLibraryMigrationState(string libraryPath, string userId, int migrationNumber, bool recordMismatchWarning = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
                return;
            if (string.IsNullOrWhiteSpace(userId))
                return; // Cannot claim ownership of a library without a user id

            string configFilePath = GetSongLibraryConfigFilePath(libraryPath);
            if (!TryReadSongLibraryMigrationState(libraryPath, out string fileOwner, out int fileState))
            {
                File.WriteAllText(configFilePath, $"{userId}\n{migrationNumber}");
                return;
            }

            if (fileOwner == "")
            {
                // Legacy file: no account recorded yet. Adopt it for the current account, keeping the higher number.
                File.WriteAllText(configFilePath, $"{userId}\n{Math.Max(fileState, migrationNumber)}");
                return;
            }

            if (fileOwner != userId)
            {
                int latestKnownNumber = LastPulledMigrations.Length > 0 ? LastPulledMigrations.Max(m => m.MigrationNumber) : 0;
                if (recordMismatchWarning)
                {
                    songLibraryOwnerWarning =
                        $"The song library \"{libraryPath}\" is registered for the account \"{fileOwner}\", but you are logged in as \"{userId}\".\n\n" +
                        "The library will be treated as up to date for your account from now on (the migration history of the other account is dropped).\n" +
                        "If that is not what you want, point this client at the correct song library or log in with the account that owns it.";
                    Console.WriteLine(songLibraryOwnerWarning);
                }
                File.WriteAllText(configFilePath, $"{userId}\n{Math.Max(latestKnownNumber, migrationNumber)}");
                return;
            }

            if (fileState >= migrationNumber)
                return; // Another client of the same account (e.g. sharing the library via NAS) was faster, never regress
            File.WriteAllText(configFilePath, $"{userId}\n{migrationNumber}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WriteSongLibraryMigrationState failed: {ex}");
        }
    }

    /// <summary>
    /// Registers the given song library for the account of the last successful pull, treating it as fully
    /// migrated for that account (its previous migration history is dropped). Meant to be called after the
    /// user explicitly agreed to take a library over that was registered for a different account.
    /// </summary>
    public void AdoptSongLibrary(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return;
        string? authedUserId = LastPulledUserId;
        if (string.IsNullOrWhiteSpace(authedUserId))
            return;

        int latestKnownNumber = LastPulledMigrations.Length > 0 ? LastPulledMigrations.Max(m => m.MigrationNumber) : 0;
        WriteSongLibraryMigrationState(libraryPath, authedUserId, latestKnownNumber, recordMismatchWarning: false);
        Console.WriteLine($"Song library \"{libraryPath}\" was taken over for account {authedUserId} (treated as fully migrated).");
    }

    /// <summary>
    /// Applies the migrations that came with the last pull to the given song library.
    /// The library contains a ".song-library.music-player-config" file that records the account the library
    /// belongs to and the number of the last migration that was applied to it. Only migrations with a
    /// higher number are applied, in order. Migrations are only applied when the recorded account matches
    /// the account of the pull; a library of another account is adopted with a warning instead (treated as
    /// fully migrated), and a missing config file means the library is assumed to be fully migrated
    /// already (only future migrations will be applied).
    /// </summary>
    public void ApplySongLibraryMigrations(string libraryPath)
    {
        if (LastPulledMigrations.Length == 0)
            return;
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return;

        try
        {
            // The account that made the pull owns the library for the purposes of this check.
            string authedUserId = LastPulledUserId ?? "";
            if (authedUserId == "")
                return;

            bool hasStateFile = TryReadSongLibraryMigrationState(libraryPath, out string fileOwner, out int state);

            if (!hasStateFile)
            {
                // Library has no migration state yet: assume it is fully up to date for the current account.
                int latestNumber = LastPulledMigrations.Max(m => m.MigrationNumber);
                WriteSongLibraryMigrationState(libraryPath, authedUserId, latestNumber);
                Console.WriteLine($"Song library has no migration state file yet, assuming it is up to date for account {authedUserId} (state {latestNumber}).");
                return;
            }

            if (fileOwner != "" && fileOwner != authedUserId)
            {
                // This library carries the migration state of a different account: the numbers are not
                // comparable, so nothing is applied and the state file is left untouched. The pull that
                // brought the migrations normally already aborts in this case (see Pull()); if we end up
                // here anyway (e.g. the library folder was set after the pull), the UI is expected to ask
                // the user whether they want to take the library over (see AdoptSongLibrary).
                songLibraryOwnerWarning =
                    $"The song library \"{libraryPath}\" is registered for the account \"{fileOwner}\", but you are logged in as \"{authedUserId}\".\n\n" +
                    "Nothing was applied to it.\n" +
                    "You can log in with the account that owns this library, point this client at another song library, " +
                    "or take the library over for your account (its migration history will be dropped).";
                Console.WriteLine(songLibraryOwnerWarning);
                return;
            }

            if (fileOwner == "")
            {
                // Legacy state file (number only, from before the account check existed): adopt it for the
                // current account without losing the recorded number, so only future migrations get applied.
                WriteSongLibraryMigrationState(libraryPath, authedUserId, state);
            }

            var pendingMigrations = LastPulledMigrations
                .Where(m => m.MigrationNumber > state)
                .OrderBy(m => m.MigrationNumber)
                .ToArray();
            if (pendingMigrations.Length == 0)
                return;

            int highestApplied = state;
            foreach (var migration in pendingMigrations)
            {
                if (migration.MigrationType == SongLibraryMigrationType.Rename)
                {
                    if (string.IsNullOrWhiteSpace(migration.OldName) || string.IsNullOrWhiteSpace(migration.NewName))
                    {
                        highestApplied = migration.MigrationNumber; // Nothing sensible to do, dont get stuck on it
                        continue;
                    }

                    // The migration refers to one specific song entry and snapshots its album/artist (a file
                    // rename does not change the tags of the file). Only rename the files that really belong
                    // to this song; other files with the same name but different tags are different songs.
                    // Entries without album/artist metadata can only be identified by their file name.
                    var filesToRename = FindSongFilesByName(libraryPath, migration.OldName)
                        .Where(f => SongFileMatchesTags(f, migration.Artist, migration.Album))
                        .ToList();

                    bool allRenamesSucceeded = true;
                    foreach (string oldFilePath in filesToRename)
                    {
                        string newFilePath = Path.Combine(Path.GetDirectoryName(oldFilePath) ?? libraryPath, migration.NewName);
                        if (File.Exists(newFilePath))
                            continue; // Target already exists, nothing to do

                        try
                        {
                            File.Move(oldFilePath, newFilePath);
                            Console.WriteLine($"Applied song library migration #{migration.MigrationNumber}: renamed \"{oldFilePath}\" to \"{newFilePath}\".");
                        }
                        catch (Exception ex)
                        {
                            if (!File.Exists(oldFilePath))
                                continue; // The file vanished in the meantime (e.g. another client already renamed it), nothing to do

                            // Keep the old state so this migration is retried on the next startup.
                            Console.WriteLine($"Could not apply song library migration #{migration.MigrationNumber} ({migration.OldName} -> {migration.NewName}): {ex}");
                            allRenamesSucceeded = false;
                        }
                    }
                    if (!allRenamesSucceeded)
                        break;
                }
                else if (migration.MigrationType == SongLibraryMigrationType.Delete)
                {
                    // The deleted entry is already gone from the database (the pull rewrote it), but the
                    // migration snapshots its album/artist. Only delete the files that really belong to this
                    // song, so the files of same-named other songs (different tags) survive. Entries without
                    // album/artist metadata can only be identified by their file name.
                    var filesToDelete = FindSongFilesByName(libraryPath, migration.OldName)
                        .Where(f => SongFileMatchesTags(f, migration.Artist, migration.Album))
                        .ToList();

                    // Delete the files from the library. Clients that share the library via a NAS usually find
                    // nothing to do here, since the deleting client already removed the file.
                    foreach (string oldFilePath in filesToDelete)
                    {
                        try
                        {
                            File.Delete(oldFilePath);
                            Console.WriteLine($"Applied song library migration #{migration.MigrationNumber}: deleted \"{oldFilePath}\".");
                        }
                        catch (Exception ex)
                        {
                            if (!File.Exists(oldFilePath))
                                continue; // The file vanished in the meantime (e.g. another client already deleted it), nothing to do

                            Console.WriteLine($"Could not apply song library migration #{migration.MigrationNumber} (delete {migration.OldName}): {ex}");
                            return;
                        }
                    }
                }

                highestApplied = migration.MigrationNumber;
            }

            // Never regress a state another client (sharing the library via NAS) already wrote in the meantime.
            if (highestApplied != state)
                WriteSongLibraryMigrationState(libraryPath, authedUserId, highestApplied);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ApplySongLibraryMigrations failed: {ex}");
        }
    }

    /// <summary>
    /// Checks whether the tags of the given song file match the album/artist of a song (same convention
    /// the database rows are filled with, see MusicPlayerSyncInterface.SongFileMatching). An empty album
    /// and artist mean the song carries no metadata and can only be identified by its file name, in which
    /// case any file with that name counts as a match. Files whose tags cannot be read only match songs
    /// without metadata.
    /// </summary>
    public static bool SongFileMatchesTags(string filePath, string artist, string album)
    {
        if (SongFileMatching.HasNoAlbumOrArtist(artist, album))
            return true; // No metadata to compare against: the file name is all the identity there is

        try
        {
            var (fileAlbum, fileArtists) = HelperFuncs.GetAlbumAndArtistsFromSong(filePath);
            return SongFileMatching.TagsEqual(artist, album, fileArtists, fileAlbum);
        }
        catch
        {
            return false; // Could not read the tags: do not touch a file that cannot be identified
        }
    }

    /// <summary>
    /// Checks whether the tags of the given song file match the given upvotedSong entry (see
    /// <see cref="SongFileMatchesTags"/>).
    /// </summary>
    public static bool SongFileMatchesEntry(string filePath, UpvotedSong entry) => SongFileMatchesTags(filePath, entry.Artist, entry.Album);

    /// <summary>
    /// Recursively finds all files with the given file name in the song library (used by the migration
    /// applier and by the rename flow, which renames every copy of the file).
    /// </summary>
    public static List<string> FindSongFilesByName(string startDir, string fileName)
    {
        List<string> foundFiles = [];
        foreach (string filePath in HelperFuncs.FindAllMp3FilesInDir(startDir))
            if (string.Equals(Path.GetFileName(filePath), fileName, StringComparison.OrdinalIgnoreCase))
                foundFiles.Add(filePath);
        return foundFiles;
    }

    public void UploadNewSongEntry(UpvotedSong newSong)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/new-song";
        // The unsynced-data queue stores the endpoint WITHOUT the version prefix (the retry logic adds it
        // back and matches "/sync/volume" against the stored string). Storing the prefixed endpoint here
        // used to produce ".../v1/v1/sync/..." URLs on retry, so queued uploads could never succeed.
        var queuedEndpoint = "/sync/new-song";
        var newSongJson = JsonSerializer.Serialize(newSong, jsonOptions);
        using var dbContext = DbWrapper.GetContext();
        try
        {
            var newSongContent = new StringContent(newSongJson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", newSongContent).Result;

            if (res.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // The same song already exists on the server under another SongId (another client of this
                // account registered the same file, or the upload went through earlier and the response
                // was lost). The response body is the existing row: redirect queued data (e.g. votes that
                // were made offline) from our local SongId of this song to the server row, so it is not
                // lost and does not get stuck in the queue. The local duplicate entry is merged away by
                // the next pull.
                try
                {
                    var canonicalSong = JsonSerializer.Deserialize<UpvotedSong>(res.Content.ReadAsStringAsync().Result, jsonOptions);
                    if (canonicalSong?.SongId != Guid.Empty && canonicalSong!.SongId != newSong.SongId)
                    {
                        int redirected = dbContext.RedirectQueuedEntriesToSong(newSong.SongId, canonicalSong!.SongId);
                        Console.WriteLine($"Song \"{newSong.Name}\" already exists on the server as {canonicalSong.SongId}, redirected {redirected} queued entr(y/ies) to it.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not read the canonical row of a rejected song upload: {ex.Message}");
                }
            }
            else if (!res.IsSuccessStatusCode)
            {
                dbContext.AddNewNotYetSyncedDataEntry(newSongJson, queuedEndpoint, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}", newSong.SongId);
            }

            State = $"UploadNewSong {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"UploadNewSong failed: {ex.Message}";

            dbContext.AddNewNotYetSyncedDataEntry(newSongJson, queuedEndpoint, ex.Message, newSong.SongId);
        }
    }

    public void Vote(SongHistoryEntry newEntry)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/vote";
        // See UploadNewSongEntry: the queue stores the endpoint without the version prefix.
        var queuedEndpoint = "/sync/vote";
        var newEntryJson = JsonSerializer.Serialize(newEntry, jsonOptions);
        using var dbContext = DbWrapper.GetContext();
        try
        {
            var newEntryContent = new StringContent(newEntryJson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", newEntryContent).Result;

            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
                dbContext.AddNewNotYetSyncedDataEntry(newEntryJson, queuedEndpoint, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}", newEntry.SongId);

            State = $"Vote {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"Vote failed: {ex.Message}";

            dbContext.AddNewNotYetSyncedDataEntry(newEntryJson, queuedEndpoint, ex.Message, newEntry.SongId);
        }
    }

    /// <summary>
    /// Starts the background worker for songs that were registered lazily during a library scan (see
    /// DbWrapperService.AddNewUpvotedSongLazy). The worker does two independent things:
    /// 1. TAG READING: reads the album/artist tags of each pending song from its file and persists them
    ///    on the row (bounded concurrency, so slow NAS reads never block the app). This only needs the
    ///    files and therefore also runs while the user is not logged in.
    /// 2. UPLOADING: posts each now-tagged song to the sync server. This only happens when a sync
    ///    session exists (client != null); otherwise the markers are simply left in place and a later
    ///    login kicks the worker again (the uploads are NOT attempted when there is no session, so a
    ///    not-logged-in first boot does not burn hundreds of pointless 401s).
    /// Crash-safe by construction:
    /// - Row + marker are written in one transaction at registration, so a killed app can neither lose
    ///   a song nor leave it without a marker.
    /// - Tags are persisted before the upload, but the marker is only removed after the upload succeeded
    ///   (or the server answered 409 for the duplicate upload), so the upload always carries the tags
    ///   and an app close between any two steps just leads to a harmless retry on the next run.
    /// songFilesByName optionally maps file names to the paths of the current library scan (used to
    /// match pending rows to their files; rows left over from a killed run are picked up here too).
    /// When it is null/empty and a song library is configured, the map is built once from the library.
    /// Never throws on the caller; failures are logged and the markers stay for the next run.
    /// </summary>
    public void ProcessPendingSongUploadsInBackground(IReadOnlyDictionary<string, IReadOnlyList<string>>? songFilesByName = null)
    {
        lock (pendingUploadWorkerLock)
        {
            if (pendingUploadWorkerRunning)
                return;
            pendingUploadWorkerRunning = true;
        }

        Task.Run(() =>
        {
            try
            {
                ProcessPendingSongUploads(songFilesByName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pending song upload worker failed: {ex}");
            }
            finally
            {
                lock (pendingUploadWorkerLock)
                    pendingUploadWorkerRunning = false;
            }
        });
    }

    void ProcessPendingSongUploads(IReadOnlyDictionary<string, IReadOnlyList<string>>? songFilesByName)
    {
        NotYetSyncedData[] pending;
        using (var listContext = DbWrapper.GetContext())
            pending = listContext.GetPendingSongUploads();
        if (pending.Length == 0)
            return;

        // When the caller did not provide the current library scan (e.g. this was kicked after a login),
        // build the file-name map from the configured library once, so tag-less rows can still be tagged.
        if ((songFilesByName == null || songFilesByName.Count == 0) && !string.IsNullOrWhiteSpace(Config.Data.SongLibraryPath) && Directory.Exists(Config.Data.SongLibraryPath))
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string filePath in HelperFuncs.FindAllMp3FilesInDir(Config.Data.SongLibraryPath))
            {
                string name = Path.GetFileName(filePath);
                if (map.TryGetValue(name, out var list))
                    ((List<string>)list).Add(filePath);
                else
                    map[name] = new List<string> { filePath };
            }
            songFilesByName = map;
        }
        songFilesByName ??= new Dictionary<string, IReadOnlyList<string>>();

        // Uploads require a sync session (created by Init/Login). Without one (e.g. first boot before
        // any login) only the tags are read and persisted; the markers stay for a later kick.
        bool canUpload = client != null;
        Console.WriteLine($"[SongUpload] Processing {pending.Length} pending song(s) in the background (tag reading: yes, uploads: {canUpload}); parallelism {PENDING_UPLOAD_WORKER_DEGREE_OF_PARALLELISM}");

        int uploaded = 0;
        int tagged = 0;
        int leftLoggedOut = 0;
        int failed = 0;
        bool authFailed = false;

        Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = PENDING_UPLOAD_WORKER_DEGREE_OF_PARALLELISM }, entry =>
        {
            try
            {
                using var dbContext = DbWrapper.GetContext();

                UpvotedSong? row = dbContext.GetUpvotedSongByIdOrNull(entry.BelongedToSongId);
                if (row == null)
                {
                    // The row was merged away or removed by a pull in the meantime; the marker is
                    // meaningless now (the song is re-registered and re-marked by the next library scan
                    // if it is still on disk and was never uploaded).
                    dbContext.RemoveNotYetSyncedDataEntries(entry);
                    return;
                }

                bool fileFound = songFilesByName.TryGetValue(row.Name, out var paths) && paths != null && paths.Count > 0;

                // 1. Tag reading + persisting (only for rows that never had their tags attempted).
                if (SongFileMatching.HasNoAlbumOrArtist(row.Artist, row.Album)
                    && fileFound
                    && entry.Error == DbWrapperService.PendingTagReadError)
                {
                    var tags = DbWrapper.ReadTagsFromSongFile(paths![0]);
                    if (SongFileMatching.HasNoAlbumOrArtist(tags.Artists, tags.Album))
                    {
                        // The file really carries no readable tags: remember that this was attempted, so
                        // it is not re-read on every scan. It stays tag-less and is uploaded tag-less as
                        // the last resort once a session exists.
                        dbContext.UpdateNotYetSyncedDataEntry(entry, null, "No readable tags (uploaded without tags when possible).");
                    }
                    else if (dbContext.TryApplyTagsToSong(row.SongId, tags.Album, tags.Artists, out bool removedAsDuplicate))
                    {
                        Interlocked.Increment(ref tagged);
                        if (removedAsDuplicate)
                        {
                            // The tags belonged to an already tagged row: our tag-less row was the
                            // duplicate and is gone now - drop its marker as well.
                            dbContext.RemoveNotYetSyncedDataEntries(entry);
                            return;
                        }
                    }
                }

                // 2. Upload (only with a sync session and only if the upload would be meaningful).
                if (!canUpload || authFailed)
                {
                    Interlocked.Increment(ref leftLoggedOut);
                    return;
                }

                string body = JsonSerializer.Serialize(row, jsonOptions);
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var res = client!.PostAsync($"{Config.Data.SyncServerHost}{ROUTE_VERSION_PREFIX}/sync/new-song", content).Result;

                if (res.IsSuccessStatusCode)
                {
                    dbContext.RemoveNotYetSyncedDataEntries(entry);
                    Interlocked.Increment(ref uploaded);
                }
                else if (res.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // The song already exists on the server (registered by another client, or this very
                    // upload went through before the app was killed): redirect queued data that still
                    // uses our SongId to the canonical row and drop the marker. The next pull merges the
                    // local duplicate away.
                    try
                    {
                        var canonicalSong = JsonSerializer.Deserialize<UpvotedSong>(res.Content.ReadAsStringAsync().Result, jsonOptions);
                        if (canonicalSong?.SongId != Guid.Empty && canonicalSong!.SongId != row.SongId)
                        {
                            int redirected = dbContext.RedirectQueuedEntriesToSong(row.SongId, canonicalSong!.SongId);
                            Console.WriteLine($"Song \"{row.Name}\" already exists on the server as {canonicalSong.SongId}, redirected {redirected} queued entr(y/ies) to it.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Could not read the canonical row of a rejected song upload: {ex.Message}");
                    }
                    dbContext.RemoveNotYetSyncedDataEntries(entry);
                    Interlocked.Increment(ref uploaded);
                }
                else if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized || res.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // No valid session (anymore): stop attempting uploads for the rest of this run, the
                    // markers stay and a login kicks the worker again.
                    authFailed = true;
                    Interlocked.Increment(ref leftLoggedOut);
                }
                else
                {
                    // Real failure (server offline, 5xx, ...): keep the marker (retried by the next run
                    // or the startup retry) and record the error on it.
                    dbContext.UpdateNotYetSyncedDataEntry(entry, null, $"{(int)res.StatusCode} {res.Content.ReadAsStringAsync().Result}");
                    Interlocked.Increment(ref failed);
                }
            }
            catch (Exception ex)
            {
                // E.g. a transient network exception: keep the marker so it is retried on the next run.
                try
                {
                    using var errorContext = DbWrapper.GetContext();
                    errorContext.UpdateNotYetSyncedDataEntry(entry, entry.Body, ex.Message);
                }
                catch (Exception logEx)
                {
                    Console.WriteLine($"Pending song upload of {entry.BelongedToSongId} failed and its error could not be recorded: {logEx.Message}");
                }
                Interlocked.Increment(ref failed);
            }
        });

        Console.WriteLine($"[SongUpload] Done: {uploaded} uploaded/deduplicated, {tagged} tagged, {leftLoggedOut} left for a later run (no session / file missing), {failed} failed and kept for retry.");
    }
}