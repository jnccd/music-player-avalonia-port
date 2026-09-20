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
/// One entry of the "most likely next" list: a song of the library and how likely the weighted choosing
/// algorithm is to pick it next (the chance is the song's share of the choosing list, see
/// <see cref="SongChoosingService.GetSongChoosingChances"/>).
/// </summary>
public record MobileSongChanceItem(string Title, string Detail, string ChanceText);

/// <summary>
/// The whole mobile client state: transport, voting, the play-chance list and the sync settings.
/// <para>
/// It drives exactly the same services the desktop client drives (all from MusicPlayerClientCore), which is
/// the point of the mobile head: playing, voting, weighting and syncing behave identically, only the UI and
/// the audio backend differ.
/// </para>
/// </summary>
public class MobileMainViewModel : MobileViewModelBase
{
    /// <summary>How many entries the "most likely next" list shows.</summary>
    const int MOST_LIKELY_SONGS_COUNT = 5;

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
    }

    // ---------- Bindable state ----------

    string statusText = "";
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }

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

    string chanceText = "—";
    public string ChanceText { get => chanceText; private set => SetProperty(ref chanceText, value); }

    string volumeNormalizationText = "";
    public string VolumeNormalizationText { get => volumeNormalizationText; private set => SetProperty(ref volumeNormalizationText, value); }

    Bitmap? coverArt;
    public Bitmap? CoverArt { get => coverArt; private set => SetProperty(ref coverArt, value); }

    bool hasCoverArt;
    public bool HasCoverArt { get => hasCoverArt; private set => SetProperty(ref hasCoverArt, value); }

    string librarySummary = "No library yet";
    public string LibrarySummary { get => librarySummary; private set => SetProperty(ref librarySummary, value); }

    public ObservableCollection<MobileSongChanceItem> MostLikelySongs { get; } = [];

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
        private set { if (SetProperty(ref loginBusy, value)) OnPropertyChanged(nameof(LoginEnabled)); }
    }
    public bool LoginEnabled => !loginBusy;

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

    double userVolume = 80;
    /// <summary>User volume in percent (the slider's unit).</summary>
    public double UserVolume
    {
        get => userVolume;
        set
        {
            if (!SetProperty(ref userVolume, value))
                return;
            volume.UserDefinedVolume = (float)(value / 100.0);
            OnPropertyChanged(nameof(VolumeText));
        }
    }
    public string VolumeText => $"{userVolume:0}%";


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
                    SetStatus("No music folder found — open Sync and set the folder by hand.");
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
                SetStatus(MostLikelySongs.Count == 0 ? "Library ready" : "Library ready — tap play");
            }
            catch (Exception ex)
            {
                MobileLog.Error("Startup failed", ex);
                SetStatus($"Startup failed: {ex.Message}");
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
            SetStatus($"Cannot play: {ex.Message}");
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
            SetStatus($"Cannot switch song: {ex.Message}");
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
            SetStatus($"Cannot switch song: {ex.Message}");
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
        SetStatus(playback.UpvoteLockedIn
            ? "Upvote locked in — it counts when this song ends"
            : "Upvote removed");
    }

    // ---------- Seeking / volume ----------

    // Seeking happens through the Progress property (see there): the seek slider is two-way bound to it and
    // a user driven change is debounced into a single seek.

    // ---------- Sync settings ----------

    /// <summary>Logs in, pushes the local account state once if the account is empty and pulls everything.</summary>
    public async Task LoginAndSyncAsync()
    {
        if (LoginBusy)
            return;

        Config.Data.SyncServerHost = ServerHost.Trim();
        Config.Data.SyncServerUsername = Username.Trim();
        Config.Save();

        string enteredPassword = Password;
        LoginBusy = true;
        SetStatus("Logging in…");
        try
        {
            await Task.Run(() => sync.Init(enteredPassword, TryCallApiInit: true));
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

            // A sync session exists now, so songs registered (and tagged) without one are uploaded.
            sync.ProcessPendingSongUploadsInBackground();

            RefreshLibrarySummary();
            RefreshMostLikelySongs();
            SetStatus(sync.State);
        }
        catch (Exception ex)
        {
            SetStatus($"Login failed: {ex.Message}");
        }
        finally
        {
            ScanRunning = false;
            LoginBusy = false;
            Password = "";
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
                SetStatus("No music folder with songs found — set the folder below.");
                return;
            }

            SetLibraryPath(libraryPath);
            ScanRunning = true;
            SetStatus("Scanning the song library…");
            await Task.Run(() => playback.UpdateAvailableSongPaths(libraryPath));
            RefreshLibrarySummary();
            RefreshMostLikelySongs();
            SetStatus($"Library scan finished ({playback.AvailableSongsCount} songs)");
        }
        catch (Exception ex)
        {
            MobileLog.Error("Library scan failed", ex);
            SetStatus($"Library scan failed: {ex.Message}");
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
            SetStatus("Enter a folder path first");
            return;
        }

        string? problem = MobilePlatform.DescribeLibraryFolderProblem(folder);
        if (problem != null)
        {
            RefreshLibraryDiagnostics();
            SetStatus($"\"{folder}\" cannot be used: {problem}");
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
        SetStatus("The song library now belongs to your account");
    }

    public void DismissLibraryOwnerWarning()
    {
        LibraryOwnerWarning = null;
        SetStatus("The song library was left untouched");
    }

    /// <summary>Opens the account registration page of the configured sync server in the browser.</summary>
    public void OpenRegistrationPage()
    {
        try
        {
            string url = sync.GetAccountRegistrationAddress(ServerHost.Trim());
            if (!MobilePlatform.OpenUrl(url))
                SetStatus("Could not open the registration page");
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot open the registration page: {ex.Message}");
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
            SetStatus(audioError);
        }
    }

    /// <summary>
    /// Refreshes everything that depends on which song is playing: title, artist, cover art, vote numbers,
    /// the song's play chance and the "most likely next" list.
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
            ChanceText = "—";
            VolumeNormalizationText = "";
            return;
        }

        SongTitle = Path.GetFileNameWithoutExtension(song.FilePath);
        RefreshSongChips(song);
        CoverArt = LoadCoverArt(song);
        HasCoverArt = CoverArt != null;

        RefreshMostLikelySongs();
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
            VoteText = "No song";
            ChanceText = "—";
            VolumeNormalizationText = "";
            return;
        }

        using var context = dbWrapper.GetContext();
        var row = context.GetUpvotedSongByIdOrNull(song.UpvotedSongId);
        if (row == null)
        {
            SongSubtitle = "Not registered yet";
            VoteText = "No votes yet";
            ChanceText = "—";
            VolumeNormalizationText = "";
            return;
        }

        SongSubtitle = BuildSubtitle(row.Artist, row.Album);
        VoteText = $"score {row.Score:0.0} · ▲ {row.TotalLikes} · ▼ {row.TotalDislikes} · streak {row.Streak}";
        VolumeNormalizationText = row.Volume > 0
            ? $"volume normalized ({row.Volume:F3})"
            : "measuring volume…";

        var chances = choosing.GetSongChoosingChances();
        ChanceText = chances.TryGetValue(row.SongId, out float chance)
            ? $"next-song chance {chance * 100:0.00}%"
            : "not in the choosing pool";
    }

    /// <summary>
    /// Rebuilds the "most likely next" list from the choosing data structure - the weighted pool the player
    /// actually draws from, so this is not a guess but the real probability of each song.
    /// </summary>
    void RefreshMostLikelySongs()
    {
        MostLikelySongs.Clear();

        try
        {
            var chances = choosing.GetSongChoosingChances();
            if (chances.Count == 0)
                return;

            using var context = dbWrapper.GetContext();
            var rowsById = context.DumpUpvotedSongs()
                .Where(row => row.SongId != Guid.Empty)
                .GroupBy(row => row.SongId)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var entry in chances
                .OrderByDescending(pair => pair.Value)
                .Take(MOST_LIKELY_SONGS_COUNT))
            {
                if (!rowsById.TryGetValue(entry.Key, out var row))
                    continue;

                string detail = BuildSubtitle(row.Artist, row.Album);
                if (detail.Length == 0)
                    detail = $"score {row.Score:0.0} · ▲ {row.TotalLikes} ▼ {row.TotalDislikes}";

                MostLikelySongs.Add(new MobileSongChanceItem(
                    Path.GetFileNameWithoutExtension(row.Name),
                    detail,
                    $"{entry.Value * 100:0.00}%"));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not build the most-likely-next list: {ex}");
        }
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
        return "no tags";
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

    void SetStatus(string text) => OnUi(() => StatusText = text);
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
