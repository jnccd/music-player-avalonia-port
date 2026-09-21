using Avalonia.Media.Imaging;
using MusicPlayerAvaloniaPort;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPortMobile.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPortMobile.ViewModels;

/// <summary>
/// One suggestion of the library search: the song and the two lines the list shows for it.
/// </summary>
public record MobileSearchResult(string Title, string Subtitle, AvailableSong Song);

/// <summary>
/// The whole mobile client state: transport, voting, the library search and the sync settings.
/// <para>
/// It drives exactly the same services the desktop client drives (all from MusicPlayerClientCore), which is
/// the point of the mobile head: playing, voting, weighting and syncing behave identically, only the UI and
/// the audio backend differ.
/// </para>
/// </summary>
public class MobileMainViewModel : MobileViewModelBase
{
    /// <summary>How many search suggestions the drop down shows.</summary>
    const int SEARCH_RESULT_COUNT = 8;

    /// <summary>What <see cref="BuildSubtitle"/> returns for a song without artist and album metadata.</summary>
    const string NoTagsText = "no tags";

    readonly SongPlaybackService playback;
    readonly SongVotingService voting;
    readonly SongChoosingService choosing;
    readonly SongSyncService sync;
    readonly SongVolumeService volume;
    readonly SongInfoService songInfo;
    readonly DbWrapperService dbWrapper;
    readonly MobileAudioPlayerService audio;

    readonly Avalonia.Threading.DispatcherTimer refreshTimer;

    /// <summary>
    /// True while the periodic refresh writes <see cref="Progress"/> from the playback position. The seek
    /// slider's two-way binding then fires its own change notification, which must not be mistaken for the
    /// user dragging the thumb (that would seek the song to where it already is on every tick).
    /// </summary>
    public bool IsUpdatingProgressFromPlayback { get; private set; }
    bool startupRunning;
    /// <summary>True once the status line reported a missing audio output (avoids repeating it every tick).</summary>
    bool audioReportedUnavailable;

    /// <summary>Position the user scrubbed to, applied once the dragging settled (see <see cref="Progress"/>).</summary>
    double pendingSeek;
    readonly Avalonia.Threading.DispatcherTimer seekDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public MobileMainViewModel()
    {
        playback = ServiceContainer.GetService<SongPlaybackService>();
        voting = ServiceContainer.GetService<SongVotingService>();
        choosing = ServiceContainer.GetService<SongChoosingService>();
        sync = ServiceContainer.GetService<SongSyncService>();
        volume = ServiceContainer.GetService<SongVolumeService>();
        songInfo = ServiceContainer.GetService<SongInfoService>();
        dbWrapper = ServiceContainer.GetService<DbWrapperService>();
        audio = ServiceContainer.GetService<MobileAudioPlayerService>();

        playback.NewSongStarted += (_, song) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnSongChanged(song));
        playback.UpvoteLockedInChanged += (_, lockedIn) => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpvoteLockedIn = lockedIn);
        audio.PlaybackStateChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPlaying = IsPlayingNow);
        volume.UserDefinedVolumeChanged += (_, value) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { UserVolume = value * 100; });
        // The loudness measurement of the playing song finishes a moment after it started, and a vote changes
        // its score - both are stored on the row, so the chips have to be re-read when that happens.
        volume.VolumeDataChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshSongChips(playback.CurrentlyPlaying));
        voting.SongGotUpvoted += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshSongChips(playback.CurrentlyPlaying));
        voting.SongGotDownvoted += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshSongChips(playback.CurrentlyPlaying));

        UserVolume = volume.UserDefinedVolume * 100;
        ServerHost = Config.Data.SyncServerHost ?? "";
        Username = Config.Data.SyncServerUsername ?? "";
        IsLoggedIn = sync.IsLoggedIn;
        LibraryPathText = Config.Data.SongLibraryPath ?? "";
        StatusText = "Starting up…";
        SongTitle = "Nothing playing";
        SongSubtitle = "Tap play to start the weighted shuffle";

        // A single timer updates everything that changes while a song plays (progress, play state, sync
        // state). Doing it here instead of binding directly to the audio backend keeps the backend free of
        // UI concerns - and the backend is polled at 2 Hz, which is invisible and costs nothing.
        refreshTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refreshTimer.Tick += (_, _) => RefreshLiveState();
        refreshTimer.Start();

        seekDebounceTimer.Tick += (_, _) =>
        {
            seekDebounceTimer.Stop();
            if (playback.CurrentlyPlaying != null)
                audio.PlayProgress = (float)pendingSeek;
        };

        // A notice is informational, not a dialog: it fades out on its own instead of staying on screen.
        noticeTimer.Tick += (_, _) =>
        {
            noticeTimer.Stop();
            HeaderNotice = null;
        };
    }

    // ---------- Bindable state ----------

    string statusText = "";
    /// <summary>
    /// The client's running log - what it is doing right now, and the outcome of the last action. Shown in the
    /// sync sheet rather than on the player screen (see <see cref="SetStatus"/>).
    /// </summary>
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }

    string? headerNotice;
    /// <summary>A failure or the result of the last tap, shown under the header and hidden again after a few
    /// seconds. Null the rest of the time, so the player screen carries no permanent status text.</summary>
    public string? HeaderNotice
    {
        get => headerNotice;
        private set { if (SetProperty(ref headerNotice, value)) OnPropertyChanged(nameof(HasHeaderNotice)); }
    }
    public bool HasHeaderNotice => !string.IsNullOrEmpty(headerNotice);

    /// <summary>How long a <see cref="HeaderNotice"/> stays on screen.</summary>
    static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);
    readonly Avalonia.Threading.DispatcherTimer noticeTimer = new() { Interval = NoticeDuration };

    string songTitle = "";
    public string SongTitle { get => songTitle; private set => SetProperty(ref songTitle, value); }

    string songSubtitle = "";
    public string SongSubtitle { get => songSubtitle; private set => SetProperty(ref songSubtitle, value); }

    string elapsedText = "0:00";
    public string ElapsedText { get => elapsedText; private set => SetProperty(ref elapsedText, value); }

    string totalText = "0:00";
    public string TotalText { get => totalText; private set => SetProperty(ref totalText, value); }

    double progress;
    /// <summary>
    /// Playback position in [0,1], two-way bound to the seek slider. A write coming from the player (the
    /// periodic refresh) is ignored here; a write coming from the slider is the user scrubbing and is
    /// forwarded to the audio backend after a short debounce, so dragging does not fire a seek per pixel -
    /// and so the skipped amount the vote scoring needs is recorded exactly once.
    /// </summary>
    public double Progress
    {
        get => progress;
        set
        {
            if (!SetProperty(ref progress, value))
                return;
            if (IsUpdatingProgressFromPlayback || playback.CurrentlyPlaying == null)
                return;

            pendingSeek = Math.Clamp(value, 0, 1);
            seekDebounceTimer.Stop();
            seekDebounceTimer.Start();
        }
    }

    bool isPlaying;
    public bool IsPlaying { get => isPlaying; private set => SetProperty(ref isPlaying, value); }

    bool upvoteLockedIn;
    public bool UpvoteLockedIn { get => upvoteLockedIn; private set => SetProperty(ref upvoteLockedIn, value); }

    string voteText = "No song";
    public string VoteText { get => voteText; private set => SetProperty(ref voteText, value); }

    Bitmap? coverArt;
    public Bitmap? CoverArt { get => coverArt; private set => SetProperty(ref coverArt, value); }

    bool hasCoverArt;
    public bool HasCoverArt { get => hasCoverArt; private set => SetProperty(ref hasCoverArt, value); }

    string librarySummary = "No library yet";
    public string LibrarySummary { get => librarySummary; private set => SetProperty(ref librarySummary, value); }

    // Settings sheet
    bool settingsOpen;
    public bool SettingsOpen { get => settingsOpen; set => SetProperty(ref settingsOpen, value); }

    string serverHost = "";
    public string ServerHost { get => serverHost; set => SetProperty(ref serverHost, value); }

    string username = "";
    public string Username { get => username; set => SetProperty(ref username, value); }

    string password = "";
    public string Password { get => password; set => SetProperty(ref password, value); }

    bool loginBusy;
    public bool LoginBusy
    {
        get => loginBusy;
        private set { if (SetProperty(ref loginBusy, value)) { OnPropertyChanged(nameof(LoginEnabled)); OnPropertyChanged(nameof(LogoutEnabled)); } }
    }
    public bool LoginEnabled => !loginBusy;

    bool isLoggedIn;
    /// <summary>Whether the client currently holds a sync session (drives the log out button).</summary>
    public bool IsLoggedIn
    {
        get => isLoggedIn;
        private set
        {
            if (!SetProperty(ref isLoggedIn, value))
                return;
            OnPropertyChanged(nameof(LogoutEnabled));
            OnPropertyChanged(nameof(SessionText));
        }
    }
    public bool LogoutEnabled => isLoggedIn && !loginBusy;

    /// <summary>"signed in as X" / "not signed in", shown above the session buttons.</summary>
    public string SessionText => isLoggedIn
        ? $"signed in as {Config.Data.SyncServerUsername ?? "?"}"
        : "not signed in";

    string libraryPathText = "";
    public string LibraryPathText { get => libraryPathText; private set => SetProperty(ref libraryPathText, value); }

    /// <summary>
    /// Folder the song library is scanned from, editable: the automatic detection only knows Android's
    /// public <c>Music</c> directory, and music on a phone is often somewhere else (a Downloads folder, an
    /// SD card, a folder a sync tool created).
    /// </summary>
    string libraryFolderInput = "";
    public string LibraryFolderInput { get => libraryFolderInput; set => SetProperty(ref libraryFolderInput, value); }

    /// <summary>Where the client looked for music and why each place was accepted or rejected.</summary>
    string libraryDiagnostics = "";
    public string LibraryDiagnostics { get => libraryDiagnostics; private set => SetProperty(ref libraryDiagnostics, value); }

    string syncState = "";
    public string SyncState { get => syncState; private set => SetProperty(ref syncState, value); }

    bool scanRunning;
    public bool ScanRunning { get => scanRunning; private set { if (SetProperty(ref scanRunning, value)) OnPropertyChanged(nameof(ScanFinished)); } }
    public bool ScanFinished => !scanRunning;

    double scanProgress;
    public double ScanProgress { get => scanProgress; private set => SetProperty(ref scanProgress, value); }

    string scanProgressText = "";
    public string ScanProgressText { get => scanProgressText; private set => SetProperty(ref scanProgressText, value); }

    /// <summary>
    /// Set when the song library folder belongs to another account (see SongSyncService.Pull). The view
    /// shows it as a banner with the option to take the library over.
    /// </summary>
    string? libraryOwnerWarning;
    public string? LibraryOwnerWarning
    {
        get => libraryOwnerWarning;
        private set { if (SetProperty(ref libraryOwnerWarning, value)) OnPropertyChanged(nameof(HasLibraryOwnerWarning)); }
    }
    public bool HasLibraryOwnerWarning => !string.IsNullOrEmpty(libraryOwnerWarning);

    /// <summary>
    /// Highest user volume the slider offers, in percent. Above 100% the audio layer amplifies (SoundFlow
    /// applies the volume as a gain, it only rejects negative values), which is what makes a quiet song or a
    /// quiet recording listenable; the final value handed to the audio backend is capped in
    /// <see cref="Services.MobileAudioPlayerService.Volume"/> so the per-song normalization cannot multiply
    /// it into clipping territory.
    /// </summary>
    public const double MaxUserVolumePercent = 200;

    double userVolume = 80;
    /// <summary>User volume in percent (the slider's unit, 0..<see cref="MaxUserVolumePercent"/>).</summary>
    public double UserVolume
    {
        get => userVolume;
        set
        {
            double clamped = Math.Clamp(value, 0, MaxUserVolumePercent);
            if (!SetProperty(ref userVolume, clamped))
                return;
            volume.UserDefinedVolume = (float)(clamped / 100.0);
            OnPropertyChanged(nameof(VolumeText));
        }
    }
    public string VolumeText => $"{userVolume:0}%";

    // ---------- Library search ----------

    string searchText = "";
    /// <summary>
    /// What the user typed into the search field. Setting it recomputes the suggestions, so the list follows
    /// every keystroke like a search box is expected to.
    /// </summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value))
                return;
            OnPropertyChanged(nameof(HasSearchText));
            RefreshSearchResults();
        }
    }
    public bool HasSearchText => !string.IsNullOrEmpty(searchText);

    /// <summary>Best matching songs for the current search text, best first.</summary>
    public ObservableCollection<MobileSearchResult> SearchResults { get; } = [];

    public bool HasSearchResults => SearchResults.Count > 0;

    /// <summary>
    /// Recomputes the suggestions with the same modified-Levenshtein matching the "play a song quickly" flow
    /// of the desktop client uses (see <see cref="SongPlaybackService.FindBestSongMatches"/>), so the ranking
    /// is "closest name first" rather than a prefix filter.
    /// </summary>
    void RefreshSearchResults()
    {
        SearchResults.Clear();

        try
        {
            if (!string.IsNullOrWhiteSpace(searchText) && playback.AvailableSongsCount > 0)
            {
                using var context = dbWrapper.GetContext();
                foreach (var (song, _) in playback.FindBestSongMatches(searchText, SEARCH_RESULT_COUNT))
                {
                    var row = context.GetUpvotedSongByIdOrNull(song.UpvotedSongId);
                    string subtitle = row == null ? "not registered yet" : BuildSubtitle(row.Artist, row.Album);
                    if (subtitle == NoTagsText)
                        subtitle = DescribeSongFolder(song);

                    SearchResults.Add(new MobileSearchResult(Path.GetFileNameWithoutExtension(song.FilePath), subtitle, song));
                }
            }
        }
        catch (Exception ex)
        {
            MobileLog.Error("Library search failed", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    /// <summary>
    /// The secondary line of a suggestion for a song without tags: where it sits inside the library, relative
    /// to the library root. Empty for songs directly in the root - then the suggestion is one line, which reads
    /// better than repeating the same folder name on every row of a single-folder library.
    /// </summary>
    static string DescribeSongFolder(AvailableSong song)
    {
        try
        {
            string? folder = Path.GetDirectoryName(song.FilePath);
            if (string.IsNullOrEmpty(folder))
                return "";

            string? libraryRoot = Config.Data.SongLibraryPath;
            if (string.IsNullOrWhiteSpace(libraryRoot))
                return Path.GetFileName(folder) ?? "";

            string relative = Path.GetRelativePath(libraryRoot, folder);
            return relative == "." ? "" : relative;
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not describe the folder of \"{song.FilePath}\": {ex.Message}");
            return "";
        }
    }

    /// <summary>Plays a suggestion and closes the search.</summary>
    public void PlaySearchResult(MobileSearchResult result)
    {
        SearchText = "";
        playback.PlaySpecificSong(result.Song);
        MobileLog.Info($"Search: playing \"{result.Title}\"");
    }

    public void ClearSearch() => SearchText = "";


    bool IsPlayingNow => audio.PlayState == SoundFlow.Enums.PlaybackState.Playing;

    // ---------- Startup ----------

    /// <summary>
    /// Startup sequence, mirroring the desktop client's song-setup thread: make sure the local database is
    /// up to date, resolve the song library folder, pull from the sync server (so pulled rows and applied
    /// song library migrations line up with the files), then scan the library. Runs on a background thread;
    /// all UI state is written through the dispatcher-safe properties.
    /// </summary>
    public void StartStartup()
    {
        if (startupRunning)
            return;
        startupRunning = true;

        Task.Run(() =>
        {
            try
            {
                MobileLog.Info("Startup: opening the song database");
                SetStatus("Preparing the song database…");
                dbWrapper.EnsureDatabaseUpToDate();

                MobileLog.Info("Startup: resolving the song library folder");
                string? libraryPath = MobilePlatform.ResolveSongLibraryPath();
                if (libraryPath == null)
                {
                    SetStatus("No music folder found — open Sync and set the folder by hand.", notify: true);
                    SetLibrarySummary("No songs found");
                    SetLibraryDiagnostics(MobilePlatform.DescribeLibraryCandidates());
                    return;
                }

                SetLibraryPath(libraryPath);
                sync.OnStateChanged = state => SetSyncState(state);

                // Startup pull like the desktop client: the local rows, the applied file migrations and the
                // scan that follows all describe the same state afterwards.
                if (!string.IsNullOrWhiteSpace(Config.Data.SyncServerHost))
                {
                    MobileLog.Info($"Startup: pulling from {Config.Data.SyncServerHost}");
                    SetStatus("Syncing with the server…");
                    sync.Pull();
                }

                SetLibraryOwnerWarning(sync.TakeSongLibraryOwnerWarning());

                MobileLog.Info($"Startup: scanning \"{libraryPath}\"");
                SetStatus("Scanning the song library…");
                ScanRunning = true;
                playback.UpdateAvailableSongPaths(libraryPath);
                ScanRunning = false;

                MobileLog.Info($"Startup: scan done, {playback.AvailableSongsCount} song(s) available");
                RefreshLibrarySummary();
                SetStatus(playback.AvailableSongsCount == 0 ? "Library ready, but it holds no songs" : "Library ready — tap play");
            }
            catch (Exception ex)
            {
                MobileLog.Error("Startup failed", ex);
                SetStatus($"Startup failed: {ex.Message}", notify: true);
            }
            finally
            {
                ScanRunning = false;
                startupRunning = false;
            }
        });
    }

    // ---------- Transport ----------

    /// <summary>
    /// Play/pause. Starts the first song when nothing is playing yet: it is picked with the weighted
    /// choosing algorithm (see <see cref="SongChoosingService.ChooseSongWithWeightedChances"/>), i.e. the
    /// same distribution the desktop client uses instead of a plain shuffle.
    /// </summary>
    public void TogglePlayPause()
    {
        try
        {
            if (playback.CurrentlyPlaying == null)
            {
                var first = choosing.ChooseSongWithWeightedChances(null);
                playback.PlaySpecificSong(first);
                return;
            }

            audio.TogglePlayPause();
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot play: {ex.Message}", notify: true);
        }
    }

    /// <summary>
    /// Next song. This is the desktop client's own path, so the vote rules apply exactly as there: a locked
    /// in upvote is cast when the song ends, and a song skipped early is downvoted.
    /// </summary>
    public void NextSong()
    {
        try
        {
            playback.GetNextSong();
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot switch song: {ex.Message}", notify: true);
        }
    }

    public void PreviousSong()
    {
        try
        {
            playback.GetPreviousSong();
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot switch song: {ex.Message}", notify: true);
        }
    }

    // ---------- Voting ----------

    /// <summary>
    /// Toggles the upvote for the current song.
    /// <para>
    /// This is the ONLY vote gesture the UI has, and it deliberately does not call
    /// <see cref="SongVotingService"/>: it flips <see cref="SongPlaybackService.UpvoteLockedIn"/> and the
    /// shared playback logic casts the vote - when the song ends or when the user skips to the next one
    /// (<see cref="SongPlaybackService.GetNextSong"/> / <see cref="SongPlaybackService.GetPreviousSong"/>).
    /// Exactly like the desktop client, where the upvote button only sets this flag.
    /// </para>
    /// <para>
    /// The other vote direction needs no button either: a song skipped early is voted down by that same
    /// shared logic, so letting the song play on and pressing next is the whole interaction.
    /// </para>
    /// </summary>
    public void ToggleUpvote()
    {
        playback.UpvoteLockedIn = !playback.UpvoteLockedIn;
        UpvoteLockedIn = playback.UpvoteLockedIn;
        MobileLog.Info($"Upvote armed: {playback.UpvoteLockedIn} (the vote itself is cast when the song ends or is skipped)");

        // No notice: the button turning accent-coloured *is* the feedback. The line still goes into the status
        // log that the sync sheet shows.
        SetStatus(playback.UpvoteLockedIn
            ? "Upvote locked in — it counts when this song ends"
            : "Upvote removed");
    }

    // ---------- Seeking / volume ----------

    // Seeking happens through the Progress property (see there): the seek slider is two-way bound to it and
    // a user driven change is debounced into a single seek.

    // ---------- Sync settings ----------

    /// <summary>
    /// Logs in and seeds the server account from the local library when the account is still empty
    /// (<c>/sync/init</c>, the whole-library upload), then pulls.
    /// </summary>
    public Task LoginAndSyncAsync() => LoginAsync(uploadLocalLibrary: true);

    /// <summary>
    /// Logs in and pulls, but sends nothing: no <c>/sync/init</c> (which would upload the whole local library
    /// to seed an empty account) and no retry of queued votes/uploads either.
    /// <para>
    /// Meant for logging into an account that is already in use, where the library upload is at best a 409 and
    /// at worst unwanted. Because a full pull against an empty account is rejected by the client before it
    /// rewrites anything, this is also safe to use by accident: the local library survives, and the ordinary
    /// log in can seed the account afterwards.
    /// </para>
    /// </summary>
    public Task LoginWithoutUploadAsync() => LoginAsync(uploadLocalLibrary: false);

    async Task LoginAsync(bool uploadLocalLibrary)
    {
        if (LoginBusy)
            return;

        Config.Data.SyncServerHost = ServerHost.Trim();
        Config.Data.SyncServerUsername = Username.Trim();
        Config.Save();

        string enteredPassword = Password;
        LoginBusy = true;
        MobileLog.Info($"Login requested (upload: {uploadLocalLibrary})");
        SetStatus(uploadLocalLibrary ? "Logging in and uploading the library…" : "Logging in (no upload)…");
        try
        {
            // TryCallApiInit is the /sync/init whole-library upload; RetryUnsyncedEntries sends queued votes
            // and song uploads. "Without uploading" turns off both.
            await Task.Run(() => sync.Init(enteredPassword, TryCallApiInit: uploadLocalLibrary, RetryUnsyncedEntries: uploadLocalLibrary));
            await Task.Run(() => sync.Pull());

            SetLibraryOwnerWarning(sync.TakeSongLibraryOwnerWarning());

            // The pull may have renamed files (song library migrations) and replaced rows, so the in-memory
            // song lists are rebuilt afterwards - exactly like the desktop client does after a login.
            if (Config.Data.SongLibraryPath is string libraryPath && Directory.Exists(libraryPath))
            {
                SetStatus("Refreshing the song library…");
                ScanRunning = true;
                await Task.Run(() => playback.UpdateAvailableSongPaths(libraryPath));
                ScanRunning = false;
            }

            // A sync session exists now, so songs registered (and tagged) without one are uploaded - unless
            // the user explicitly asked for a login that uploads nothing.
            if (uploadLocalLibrary)
                sync.ProcessPendingSongUploadsInBackground();

            RefreshLibrarySummary();
            SetStatus(sync.State);
        }
        catch (Exception ex)
        {
            MobileLog.Error("Login failed", ex);
            SetStatus($"Login failed: {ex.Message}", notify: true);
        }
        finally
        {
            ScanRunning = false;
            LoginBusy = false;
            Password = "";
            IsLoggedIn = sync.IsLoggedIn;
        }
    }

    /// <summary>
    /// Ends the sync session (the "Log out" button). The local library and the configured account name stay, so
    /// the player keeps working offline and logging back in as the same account continues where it left off.
    /// </summary>
    public void Logout()
    {
        try
        {
            sync.Logout();
            MobileLog.Info("Logged out");
            SetStatus(sync.State, notify: true); // direct response to the tap
        }
        catch (Exception ex)
        {
            MobileLog.Error("Log out failed", ex);
            SetStatus($"Log out failed: {ex.Message}", notify: true);
        }
        finally
        {
            IsLoggedIn = sync.IsLoggedIn;
        }
    }

    /// <summary>Rescans the configured library folder (the settings sheet's "Rescan" button).</summary>
    public async Task RescanLibraryAsync()
    {
        if (ScanRunning)
            return;

        try
        {
            string? libraryPath = MobilePlatform.ResolveSongLibraryPath();
            RefreshLibraryDiagnostics();
            if (libraryPath == null)
            {
                SetStatus("No music folder with songs found — set the folder below.", notify: true);
                return;
            }

            SetLibraryPath(libraryPath);
            ScanRunning = true;
            SetStatus("Scanning the song library…");
            await Task.Run(() => playback.UpdateAvailableSongPaths(libraryPath));
            RefreshLibrarySummary();
            SetStatus($"Library scan finished ({playback.AvailableSongsCount} songs)", notify: true);
        }
        catch (Exception ex)
        {
            MobileLog.Error("Library scan failed", ex);
            SetStatus($"Library scan failed: {ex.Message}", notify: true);
        }
        finally
        {
            ScanRunning = false;
        }
    }

    /// <summary>
    /// Takes the folder the user typed in the settings sheet as the song library and scans it. This is the
    /// way out when Android's public music directory is not where the music is - the app cannot guess that.
    /// </summary>
    public async Task UseEnteredLibraryFolderAsync()
    {
        string folder = LibraryFolderInput.Trim();
        if (folder.Length == 0)
        {
            SetStatus("Enter a folder path first", notify: true);
            return;
        }

        string? problem = MobilePlatform.DescribeLibraryFolderProblem(folder);
        if (problem != null)
        {
            RefreshLibraryDiagnostics();
            SetStatus($"\"{folder}\" cannot be used: {problem}", notify: true);
            return;
        }

        Config.Data.SongLibraryPath = folder;
        Config.Save();
        SetLibraryPath(folder);
        MobileLog.Info($"Song library set by hand to \"{folder}\"");
        await RescanLibraryAsync();
    }

    /// <summary>Refreshes the "where did we look" diagnostics shown in the settings sheet.</summary>
    public void RefreshLibraryDiagnostics()
    {
        try
        {
            LibraryDiagnostics = MobilePlatform.DescribeLibraryCandidates();
        }
        catch (Exception ex)
        {
            LibraryDiagnostics = $"Could not inspect the folders: {ex.Message}";
        }
    }

    /// <summary>Takes the song library over for the logged in account (see the owner warning banner).</summary>
    public void AdoptSongLibrary()
    {
        if (string.IsNullOrWhiteSpace(Config.Data.SongLibraryPath))
            return;

        sync.AdoptSongLibrary(Config.Data.SongLibraryPath);
        LibraryOwnerWarning = null;
        SetStatus("The song library now belongs to your account", notify: true);
    }

    public void DismissLibraryOwnerWarning()
    {
        LibraryOwnerWarning = null;
        SetStatus("The song library was left untouched", notify: true);
    }

    /// <summary>Opens the account registration page of the configured sync server in the browser.</summary>
    public void OpenRegistrationPage()
    {
        try
        {
            string url = sync.GetAccountRegistrationAddress(ServerHost.Trim());
            if (!MobilePlatform.OpenUrl(url))
                SetStatus("Could not open the registration page", notify: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot open the registration page: {ex.Message}", notify: true);
        }
    }

    // ---------- Live UI refresh ----------

    /// <summary>
    /// Updates everything that changes while a song plays. Runs on the UI thread (the timer is created on
    /// it), so the properties can be written directly.
    /// </summary>
    void RefreshLiveState()
    {
        IsPlaying = IsPlayingNow;

        float? duration = audio.SongDurationSeconds;
        float? progressValue = audio.PlayProgress;

        if (duration is float seconds && seconds > 0)
        {
            TotalText = FormatTime(seconds);
            if (progressValue is float value && !float.IsNaN(value) && !float.IsInfinity(value))
            {
                // While the user is scrubbing, the thumb belongs to them - writing the playback position
                // would fight the drag and make it jump.
                if (!seekDebounceTimer.IsEnabled)
                {
                    // The flag spans the write only: the slider's binding updates synchronously with it, so
                    // the Progress setter can tell "the player moved the thumb" from "the user dragged it".
                    IsUpdatingProgressFromPlayback = true;
                    try
                    {
                        Progress = Math.Clamp(value, 0, 1);
                    }
                    finally
                    {
                        IsUpdatingProgressFromPlayback = false;
                    }
                }
                ElapsedText = FormatTime(value * seconds);
            }
        }

        if (!startupRunning && !ScanRunning && !LoginBusy && sync.State.Length > 0)
            SyncState = sync.State;

        // An audio output that could not be opened must not be silent about it - the player otherwise just
        // looks like it ignores the play button.
        if (!audioReportedUnavailable && !audio.IsAvailable && audio.LastError is { Length: > 0 } audioError)
        {
            audioReportedUnavailable = true;
            SetStatus(audioError, notify: true);
        }
    }

    /// <summary>
    /// Refreshes everything that depends on which song is playing: title, artist, cover art and vote numbers.
    /// </summary>
    void OnSongChanged(AvailableSong? song)
    {
        if (song == null)
        {
            SongTitle = "Nothing playing";
            SongSubtitle = "";
            CoverArt = null;
            HasCoverArt = false;
            VoteText = "No song";
            return;
        }

        SongTitle = Path.GetFileNameWithoutExtension(song.FilePath);
        RefreshSongChips(song);
        CoverArt = LoadCoverArt(song);
        HasCoverArt = CoverArt != null;
    }

    /// <summary>
    /// Refreshes the per-song facts the chips show (votes, play chance, volume normalization) from the
    /// database. Separate from <see cref="OnSongChanged"/> because the loudness measurement finishes *after*
    /// the song started: without this the chip would keep saying "measuring volume…" for a song that was
    /// measured seconds ago (see the SongVolumeService.VolumeDataChanged subscription).
    /// </summary>
    void RefreshSongChips(AvailableSong? song)
    {
        if (song == null)
        {
            SongSubtitle = "";
            VoteText = "No song";
            return;
        }

        using var context = dbWrapper.GetContext();
        var row = context.GetUpvotedSongByIdOrNull(song.UpvotedSongId);
        if (row == null)
        {
            SongSubtitle = "Not registered yet";
            VoteText = "No votes yet";
            return;
        }

        SongSubtitle = BuildSubtitle(row.Artist, row.Album);
        VoteText = $"score {row.Score:0.0} · ▲ {row.TotalLikes} · ▼ {row.TotalDislikes} · streak {row.Streak}";
    }

    void RefreshLibrarySummary()
    {
        try
        {
            int available = playback.AvailableSongsCount;
            using var context = dbWrapper.GetContext();
            int upvoted = context.DumpUpvotedSongs().Length;
            LibrarySummary = $"{available} songs · {upvoted} upvoted entries";
        }
        catch (Exception ex)
        {
            LibrarySummary = $"Library summary unavailable: {ex.Message}";
        }
    }

    static string BuildSubtitle(string? artist, string? album)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(artist);
        bool hasAlbum = !string.IsNullOrWhiteSpace(album);
        if (hasArtist && hasAlbum)
            return $"{artist} — {album}";
        if (hasArtist)
            return artist!;
        if (hasAlbum)
            return album!;
        return NoTagsText;
    }

    Bitmap? LoadCoverArt(AvailableSong song)
    {
        try
        {
            byte[]? bytes = songInfo.GetCoverArtBytesOfSong(song);
            if (bytes == null || bytes.Length == 0)
                return null;

            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            // A broken/unsupported embedded image must not take the player down - the view then shows its
            // placeholder artwork instead.
            Console.WriteLine($"Could not load the cover art of \"{song.FilePath}\": {ex.Message}");
            return null;
        }
    }

    static string FormatTime(float seconds)
    {
        if (seconds < 0 || float.IsNaN(seconds) || float.IsInfinity(seconds))
            return "0:00";

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    // ---------- Thread safe state setters (the startup/login/scan work runs off the UI thread) ----------

    /// <summary>
    /// Records what the client is doing. <see cref="StatusText"/> is the running log and is shown in the sync
    /// sheet; <paramref name="notify"/> additionally surfaces it under the header for a few seconds.
    /// <para>
    /// Only failures and the direct result of a tap notify. Steady state ("Library ready — tap play", the
    /// startup stages) does not: it is not worth a permanent line on a player screen, and the
    /// "N songs · M upvoted entries" line at the bottom already says the library is loaded.
    /// </para>
    /// </summary>
    void SetStatus(string text, bool notify = false) => OnUi(() =>
    {
        StatusText = text;

        if (!notify)
            return;

        HeaderNotice = text;
        noticeTimer.Stop();
        noticeTimer.Start();
    });

    void SetSyncState(string text) => OnUi(() => SyncState = text);
    void SetLibraryPath(string text) => OnUi(() => { LibraryPathText = text; LibraryFolderInput = text; });
    void SetLibrarySummary(string text) => OnUi(() => LibrarySummary = text);
    void SetLibraryDiagnostics(string text) => OnUi(() => LibraryDiagnostics = text);
    void SetLibraryOwnerWarning(string? warning) => OnUi(() => LibraryOwnerWarning = warning);

    static void OnUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            action();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }
}
