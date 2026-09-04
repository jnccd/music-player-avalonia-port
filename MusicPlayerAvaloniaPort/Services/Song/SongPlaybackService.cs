using Avalonia.Diagnostics;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongPlaybackService))]
public class SongPlaybackService
{
    readonly AudioLibWrapperService AudioLibWrapper;
    readonly SongVotingService SongVotingService;
    readonly SongChoosingService SongChoosingService;
    readonly DbWrapperService DbWrapper;

    readonly List<AvailableSong> AvailableSongs = [];

    // Serializes "the song is not registered yet, so register it" (see CreateAvailableSong): with a
    // parallel library scan two files of the very same song must not race and insert duplicate rows.
    readonly object songRegistrationLock = new();
    // Guards the progress bar value against out-of-order writes from the parallel scan tasks.
    readonly object progressLock = new();

    int RuntimePlayHistoryIndex = 0;
    readonly List<AvailableSong> RuntimePlayHistory = [];
    public AvailableSong? CurrentlyPlaying => RuntimePlayHistoryIndex >= 0 && RuntimePlayHistoryIndex < RuntimePlayHistory.Count ?
        RuntimePlayHistory[RuntimePlayHistoryIndex] :
        null;
    public bool UpvoteLockedIn
    {
        get;
        set
        {
            field = value;
            UpvoteLockedInChanged?.Invoke(this, value);
        }
    } = false;
    public float UpdateAvailableSongPathsProgress { get; private set; } = 0;

    public event EventHandler<AvailableSong>? NewSongStarted;
    public event EventHandler<bool>? UpvoteLockedInChanged;

    public SongPlaybackService(AudioLibWrapperService AudioLibWrapper, SongVotingService UpvotedSongManager, SongChoosingService SongChoosingService, DbWrapperService DbWrapper)
    {
        this.AudioLibWrapper = AudioLibWrapper;
        AudioLibWrapper.PlaybackEnded += (sender, args) =>
        {
            GetNextSong();
        };

        this.SongVotingService = UpvotedSongManager;
        this.SongChoosingService = SongChoosingService;
        this.DbWrapper = DbWrapper;
    }

    public void UpdateAvailableSongPaths(string libraryRootPath)
    {
        UpdateAvailableSongPathsProgress = 0;
        AvailableSongs.Clear();
        var mp3Files = HelperFuncs.FindAllMp3FilesInDir(libraryRootPath);
        UpdateAvailableSongPathsProgress = 0.33f;

        // Resolving and registering the songs is independent per file, so the scan runs in parallel
        // (the per-file database lookup is a small read; registering a brand new song is serialized and
        // re-checks inside a lock, see CreateAvailableSong, so two files of the very same song can never
        // race and insert duplicate rows). The results are collected into an indexed array and added in
        // order afterwards, so the final list order is deterministic.
        var availableSongs = new AvailableSong[mp3Files.Count];
        int completedCount = 0;
        int totalCount = mp3Files.Count;

        try
        {
            Parallel.For(0, totalCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16) }, i =>
            {
                availableSongs[i] = CreateAvailableSong(mp3Files[i]);

                // The progress is based on a completion counter and only ever raised, so it cannot wobble
                // backwards when files finish out of order.
                int completed = Interlocked.Increment(ref completedCount);
                lock (progressLock)
                {
                    float progress = 0.33f + 0.33f * completed / totalCount;
                    if (progress > UpdateAvailableSongPathsProgress)
                        UpdateAvailableSongPathsProgress = progress;
                }
            });
        }
        catch (AggregateException ex)
        {
            // Preserve the serial behavior: the first failing file aborts the scan with its exception.
            throw ex.Flatten().InnerExceptions[0];
        }

        AvailableSongs.AddRange(availableSongs);

        UpdateAvailableSongPathsProgress = 0.66f;
        SongChoosingService.CreateSongChoosingDataStructure(AvailableSongs);
        UpdateAvailableSongPathsProgress = 1.0f;
    }
    AvailableSong CreateAvailableSong(string fullPath)
    {
        UpvotedSong? upvotedSong;
        using (var dbContext = DbWrapper.GetContext())
            upvotedSong = dbContext.GetUpvotedSongByFullPath(fullPath);

        if (upvotedSong == null)
        {
            // The song is not registered yet. Registering writes to the database and uploads the song,
            // so it is serialized: when the scan runs in parallel, another file of the very same song
            // (e.g. copies in several folders) could otherwise race past the "not registered" check and
            // insert a duplicate row. The lookup is repeated inside the lock, so the first registration
            // wins and the second file maps to the same row.
            lock (songRegistrationLock)
            {
                using (var dbContext = DbWrapper.GetContext())
                    upvotedSong = dbContext.GetUpvotedSongByFullPath(fullPath);
                upvotedSong ??= SongVotingService.RegisterNewUpvotedSong(fullPath);
            }
        }

        return new AvailableSong(fullPath, upvotedSong.SongId);
    }
    public AvailableSong? RegisterNewSong(string fullPath)
    {
        var newAvailableSong = CreateAvailableSong(fullPath);
        AvailableSongs.Add(newAvailableSong);

        SongChoosingService.CreateSongChoosingDataStructure(AvailableSongs);

        return newAvailableSong;
    }
    /// <summary>
    /// Swaps the file paths of available songs after their files got renamed in the song library (a song
    /// can exist as multiple copies in different subfolders, so several entries can be affected). The
    /// upvotedSong ids stay the same, since the database entries keep their identity. Entries in the
    /// runtime play history are updated as well, so replaying them uses the new path.
    /// </summary>
    public void RenameSongFiles(IReadOnlyCollection<(string OldPath, string NewPath)> renamedFiles)
    {
        if (renamedFiles.Count == 0)
            return;

        var pathMap = new Dictionary<string, string>();
        foreach (var (oldPath, newPath) in renamedFiles)
            pathMap[oldPath] = newPath;

        for (int i = 0; i < AvailableSongs.Count; i++)
            if (pathMap.ContainsKey(AvailableSongs[i].FilePath))
                AvailableSongs[i] = new AvailableSong(pathMap[AvailableSongs[i].FilePath], AvailableSongs[i].UpvotedSongId);

        lock (RuntimePlayHistory)
        {
            for (int i = 0; i < RuntimePlayHistory.Count; i++)
                if (pathMap.ContainsKey(RuntimePlayHistory[i].FilePath))
                    RuntimePlayHistory[i] = new AvailableSong(pathMap[RuntimePlayHistory[i].FilePath], RuntimePlayHistory[i].UpvotedSongId);
        }

        SongChoosingService.CreateSongChoosingDataStructure(AvailableSongs);
    }
    public AvailableSong? FindAvailableSong(string fileNameWithoutExtension)
    {
        var foundSong = AvailableSongs
            .FirstOrDefault(s =>
                s.FilePath.Split('/', Path.DirectorySeparatorChar, '\\', '.')
                .Contains(fileNameWithoutExtension));

        return foundSong;
    }
    public AvailableSong? FindAvailableSong(Guid upvotedSongId)
    {
        var foundSong = AvailableSongs
            .FirstOrDefault(s =>
                s.UpvotedSongId == upvotedSongId);

        return foundSong;
    }
    public int AvailableSongsCount => AvailableSongs.Count;

    public void PlaySpecificSong(AvailableSong availableSong, float? secondToStartAt = null)
    {
        lock (RuntimePlayHistory)
        {
            // Update RuntimePlayHistory
            RuntimePlayHistory.Add(availableSong);
            RuntimePlayHistoryIndex = RuntimePlayHistory.Count - 1;

            // Invoke Events
            AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"), GetSampleReadingStrategyForSong(CurrentlyPlaying));
            NewSongStarted?.Invoke(this, CurrentlyPlaying);

            if (secondToStartAt != null)
                AudioLibWrapper.PlayProgress = secondToStartAt / AudioLibWrapper.SongDurationSeconds;
        }
    }
    public void GetNextSong()
    {
        lock (RuntimePlayHistory)
        {
            // Score Change
            if (UpvoteLockedIn)
            {
                SongVotingService.UpvoteSong(CurrentlyPlaying
                    ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                    AvailableSongs);
                UpvoteLockedIn = false;
            }
            else if (RuntimePlayHistoryIndex > 0
                && RuntimePlayHistoryIndex == RuntimePlayHistory.Count - 1 // Last Song in filled RuntimePlayHistory
                && (1 - AudioLibWrapper.PlayProgress) * AudioLibWrapper.SongDurationSeconds > 1)
            {
                SongVotingService.DownvoteSong(CurrentlyPlaying
                    ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                    AvailableSongs);
            }

            // Update RuntimePlayHistory
            RuntimePlayHistoryIndex++;
            while (RuntimePlayHistoryIndex >= RuntimePlayHistory.Count)
            {
                var nextSong = ChooseNextSong();
                RuntimePlayHistory.Add(nextSong);
            }

            // Invoke Events
            AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"), GetSampleReadingStrategyForSong(CurrentlyPlaying));
            NewSongStarted?.Invoke(this, CurrentlyPlaying);
        }
    }
    public void GetPreviousSong()
    {
        lock (RuntimePlayHistory)
        {
            // Score Change
            if (UpvoteLockedIn)
            {
                SongVotingService.UpvoteSong(CurrentlyPlaying
                    ?? throw new InvalidDataException("No currently playing song in GetNextSong()!"),
                    AvailableSongs);
                UpvoteLockedIn = false;
            }

            // Update RuntimePlayHistory
            RuntimePlayHistoryIndex--;
            while (RuntimePlayHistoryIndex < 0)
            {
                var newPreviousSong = ChooseNextSong();
                RuntimePlayHistory.Insert(0, newPreviousSong);
                RuntimePlayHistoryIndex++;
            }

            // Invoke Events
            AudioLibWrapper.PlaySong(CurrentlyPlaying?.FilePath ?? throw new InvalidDataException("No song to play"), GetSampleReadingStrategyForSong(CurrentlyPlaying));
            NewSongStarted?.Invoke(this, CurrentlyPlaying);
        }
    }

    AvailableSong ChooseNextSong()
    {
        var newSong = SongChoosingService.ChooseSongWithWeightedChances(CurrentlyPlaying);
        return newSong;
    }
    SampleReadingStrategy GetSampleReadingStrategyForSong(AvailableSong song)
    {
        using var dbContext = DbWrapper.GetContext();
        return dbContext.DoesSongHaveVolume(song.UpvotedSongId) ? SampleReadingStrategy.DirectRead : SampleReadingStrategy.GlobalArray;
    }
}