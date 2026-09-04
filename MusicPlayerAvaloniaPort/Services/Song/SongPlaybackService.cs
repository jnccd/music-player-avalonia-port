using Avalonia.Diagnostics;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface;
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
    readonly SongSyncService SyncService;

    readonly List<AvailableSong> AvailableSongs = [];

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

    public SongPlaybackService(AudioLibWrapperService AudioLibWrapper, SongVotingService UpvotedSongManager, SongChoosingService SongChoosingService, DbWrapperService DbWrapper, SongSyncService SyncService)
    {
        this.AudioLibWrapper = AudioLibWrapper;
        AudioLibWrapper.PlaybackEnded += (sender, args) =>
        {
            GetNextSong();
        };

        this.SongVotingService = UpvotedSongManager;
        this.SongChoosingService = SongChoosingService;
        this.DbWrapper = DbWrapper;
        this.SyncService = SyncService;
    }

    public void UpdateAvailableSongPaths(string libraryRootPath)
    {
        UpdateAvailableSongPathsProgress = 0;
        AvailableSongs.Clear();
        var mp3Files = HelperFuncs.FindAllMp3FilesInDir(libraryRootPath);
        UpdateAvailableSongPathsProgress = 0.33f;

        // Resolving and registering the songs is independent per file, so the scan runs in parallel.
        // Brand new songs are registered WITHOUT reading their tags (on slow media such as a NAS that
        // read takes hundreds of ms per file): the insert + a durable pending-upload marker are written
        // in one transaction (SongVotingService.RegisterNewUpvotedSong + DbWrapperService), and the tag
        // read + upload happen afterwards in the background (see SyncService.ProcessPendingSongUploads
        // InBackground, kicked off below). The results are collected into an indexed array and added in
        // order afterwards, so the final list order is deterministic.
        //
        // Files whose name occurs MORE THAN ONCE in the library are the exception: for them the file
        // name alone cannot identify the song, so their tags are read right away (strict identity, see
        // CreateAmbiguousNameAvailableSong) - lazily tagging them later would be ambiguous anyway, since
        // the worker could not know which copy a row belongs to. This only costs tag reads for the rare
        // duplicate-named files.
        var ambiguousNames = mp3Files
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var availableSongs = new AvailableSong[mp3Files.Count];
        int completedCount = 0;
        int totalCount = mp3Files.Count;

        try
        {
            Parallel.For(0, totalCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16) }, i =>
            {
                availableSongs[i] = CreateAvailableSong(mp3Files[i], deferTagReadingAndUpload: true, ambiguousLibraryNames: ambiguousNames);

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

        // Songs registered lazily during this scan (and leftovers of a previously killed run) are tagged
        // and uploaded in the background now - the app is already fully usable, the reads just must not
        // block it. The file-name map comes from this scan's enumeration.
        var filesByName = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in mp3Files)
        {
            string name = Path.GetFileName(filePath);
            if (filesByName.TryGetValue(name, out var list))
                ((List<string>)list).Add(filePath);
            else
                filesByName[name] = new List<string> { filePath };
        }
        SyncService.ProcessPendingSongUploadsInBackground(filesByName);
    }
    AvailableSong CreateAvailableSong(string fullPath, bool deferTagReadingAndUpload = false, IReadOnlySet<string>? ambiguousLibraryNames = null)
    {
        // When several files of the library share this file name, the name alone cannot identify which
        // row (if any) the file belongs to - its tags have to decide. Those files are resolved strictly
        // with their tags read now.
        if (ambiguousLibraryNames != null && ambiguousLibraryNames.Contains(Path.GetFileName(fullPath)))
            return CreateAmbiguousNameAvailableSong(fullPath);

        // The database lookup is a quick read and is the common case for already registered songs.
        // Registering a brand new song happens in SongVotingService.RegisterNewUpvotedSong: during a
        // library scan (deferTagReadingAndUpload) it only inserts the row and defers the slow tag read
        // and the upload to the background worker; single-song registrations while the app runs read
        // the tags and upload immediately as before.
        using var dbContext = DbWrapper.GetContext();
        var upvotedSong = dbContext.GetUpvotedSongByFullPath(fullPath);
        upvotedSong ??= SongVotingService.RegisterNewUpvotedSong(fullPath, deferTagReadingAndUpload);

        return new AvailableSong(fullPath, upvotedSong.SongId);
    }

    /// <summary>
    /// Resolves a file whose name is shared by several files of the library. The tags of THIS file are
    /// read first and decide between three cases:
    /// - an existing row carries exactly these tags: the file belongs to it (identical copies of the
    ///   same song share the row);
    /// - the file carries no readable tags and a metadata-less row of the same name exists: it belongs
    ///   to that row;
    /// - otherwise the file is a distinct same-named song: a NEW row is registered with its tags
    ///   (RegisterUpvotedSongWithTags), so different songs of the same name stay distinguishable from
    ///   the very first scan on.
    /// Never lazy: such rows must carry their real tags (a lazy row could not be matched back to the
    /// right copy later).
    /// </summary>
    AvailableSong CreateAmbiguousNameAvailableSong(string fullPath)
    {
        string fileName = Path.GetFileName(fullPath);
        var tags = DbWrapper.ReadTagsFromSongFile(fullPath);

        UpvotedSong? row;
        using (var dbContext = DbWrapper.GetContext())
        {
            var sameNameRows = dbContext.GetUpvotedSongsByName(fileName);
            bool fileHasTags = !SongFileMatching.HasNoAlbumOrArtist(tags.Artists, tags.Album);

            if (sameNameRows.Length == 0)
            {
                row = null;
            }
            else if (fileHasTags)
            {
                var exactMatches = sameNameRows
                    .Where(s => SongFileMatching.TagsEqual(s.Artist, s.Album, tags.Artists, tags.Album))
                    .ToArray();
                if (exactMatches.Length > 0)
                {
                    // Identical copies of the same song share the row (identical duplicates: pick the
                    // canonical one deterministically).
                    row = SongFileMatching.ChooseCanonicalEntry(exactMatches, tags.Album, tags.Artists);
                }
                else
                {
                    // No row carries these tags. A single metadata-less row is the historical catch-all
                    // for this name (rows from other clients/pulls); with any other rows present the
                    // file is a new, distinct same-named song.
                    var metadataLessRows = sameNameRows.Where(s => SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album)).ToArray();
                    row = metadataLessRows.Length == 1 && sameNameRows.Length == 1 ? metadataLessRows[0] : null;
                }
            }
            else
            {
                // The file carries no readable tags: bind it to a metadata-less row of the same name if
                // one exists, otherwise it is a new (metadata-less) song.
                var metadataLessRows = sameNameRows.Where(s => SongFileMatching.HasNoAlbumOrArtist(s.Artist, s.Album)).ToArray();
                row = metadataLessRows.Length > 0 ? SongFileMatching.ChooseCanonicalEntry(metadataLessRows) : null;
            }
        }

        row ??= SongVotingService.RegisterUpvotedSongWithTags(fullPath, tags.Album, tags.Artists);
        return new AvailableSong(fullPath, row.SongId);
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