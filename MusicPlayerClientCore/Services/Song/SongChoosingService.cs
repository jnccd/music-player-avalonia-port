using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongChoosingService))]
public class SongChoosingService(DbWrapperService DbWrapper)
{
    List<AvailableSong> SongChoosingList = [];
    public float CreateSongChoosingDataStructureProgress { get; private set; } = 0;

    public AvailableSong ChooseSongWithWeightedChances(AvailableSong? currentSongThatShouldntBeRepeated)
    {
        lock (SongChoosingList)
        {
            int SongChoosingListIndex;
            do
                SongChoosingListIndex = Random.Shared.Next(SongChoosingList.Count);
            while (SongChoosingList[SongChoosingListIndex] == currentSongThatShouldntBeRepeated);

            return SongChoosingList[SongChoosingListIndex];
        }
    }

    public void CreateSongChoosingDataStructure(List<AvailableSong> AvailableSongs)
    {
        lock (SongChoosingList)
        {
            using var dbContext = DbWrapper.GetContext();

            // One query for all rows instead of an EF lookup per song (a full library costs ~3.5k queries
            // this way), and the per-song amounts are computed first so the list can be created with its
            // final capacity in one go - the old code grew the list entry by entry, reallocating the
            // backing array again and again (the list holds one entry per chance unit, ~76k for a full
            // library). This runs on every library scan and on every new song registration.
            var rowsBySongId = new Dictionary<Guid, UpvotedSong>();
            foreach (var row in dbContext.DumpUpvotedSongs())
                if (row.SongId != Guid.Empty)
                    rowsBySongId[row.SongId] = row;

            var entryCountsPerSong = new int[AvailableSongs.Count];
            int totalEntries = 0;
            for (int i = 0; i < AvailableSongs.Count; i++)
            {
                var availableSong = AvailableSongs[i];
                var upvotedSong = availableSong.UpvotedSongId is Guid songId && rowsBySongId.TryGetValue(songId, out var row)
                    ? row
                    : throw new InvalidDataException($"SongId {availableSong.UpvotedSongId} not found!");

                // Ceiling matches the "k < amount" loop this replaces (amount is a float).
                entryCountsPerSong[i] = (int)Math.Ceiling(GetTargetSongChoosingAmount(upvotedSong, AvailableSongs));
                totalEntries += entryCountsPerSong[i];

                CreateSongChoosingDataStructureProgress = (i + 1) / (float)AvailableSongs.Count;
            }

            SongChoosingList.Clear();
            if (SongChoosingList.Capacity < totalEntries)
                SongChoosingList.Capacity = totalEntries;

            for (int i = 0; i < AvailableSongs.Count; i++)
            {
                var availableSong = AvailableSongs[i];
                for (int k = 0; k < entryCountsPerSong[i]; k++)
                    SongChoosingList.Add(availableSong);
            }

#if DEBUG
            TestChoosingListIntegrity(AvailableSongs);
#endif
        }
    }

    public void UpdateSongChoosingDataStructure(AvailableSong songToUpdateListFor, IReadOnlyList<AvailableSong> AvailableSongs)
    {
        lock (SongChoosingList)
        {
            using var dbContext = DbWrapper.GetContext();

            // Getting Choosing List Count
            int index = SongChoosingList.FindIndex(x => x == songToUpdateListFor);
            if (index == -1)
                return;
            int i = index;
            while (i < SongChoosingList.Count && SongChoosingList[i] == songToUpdateListFor)
                i++;
            int count = i - index;

            // Getting target Count
            var upvotedSong = dbContext.GetUpvotedSongById(songToUpdateListFor.UpvotedSongId);
            float amount = GetTargetSongChoosingAmount(upvotedSong, AvailableSongs);

            // Grow/shrink the song's block in a single list operation instead of entry by entry: every
            // List.Insert/RemoveAt shifts the whole tail of the list, so a bigger delta (a first upvote
            // flips the like/dislike ratio and can add ~50 * 1000/songCount entries at once) used to move
            // the list hundreds of thousands of times. Ceiling matches the "j < amount - count" loops this
            // replaces.
            if (amount > count)
                SongChoosingList.InsertRange(index, Enumerable.Repeat(songToUpdateListFor, (int)Math.Ceiling(amount - count)));
            else if (amount < count)
                SongChoosingList.RemoveRange(index, (int)Math.Ceiling(count - amount));

#if DEBUG
            TestChoosingListIntegrity(AvailableSongs);
#endif
        }
    }

    public float GetSongChoosingChance(AvailableSong? songToCheckChanceFor)
    {
        if (songToCheckChanceFor == null)
            return 0;

        lock (SongChoosingList)
        {
            // Getting Choosing List Count
            int index = SongChoosingList.FindIndex(x => x == songToCheckChanceFor);
            if (index == -1)
                return 0;
            int i = index;
            while (i < SongChoosingList.Count && SongChoosingList[i] == songToCheckChanceFor)
                i++;
            int count = i - index;

            return count / (float)SongChoosingList.Count;
        }
    }

    /// <summary>
    /// The play chance (0..1) of every song of the choosing data structure, keyed by song id. Unlike
    /// <see cref="GetSongChoosingChance"/> - which scans the choosing list once per call, which is fine
    /// for the statistics grid because it only asks for the rows it is about to draw - this counts all
    /// songs in ONE pass over the list. Views that need the chance of every song (the export library
    /// view's "minimal play chance" threshold) would otherwise do a quadratic scan over a list of ~76k
    /// entries and freeze for seconds.
    /// </summary>
    public Dictionary<Guid, float> GetSongChoosingChances()
    {
        var chances = new Dictionary<Guid, float>();
        lock (SongChoosingList)
        {
            if (SongChoosingList.Count == 0)
                return chances;

            foreach (var song in SongChoosingList)
                if (song.UpvotedSongId is Guid songId)
                    chances[songId] = chances.TryGetValue(songId, out float entries) ? entries + 1 : 1;

            // The list holds one entry per chance unit, so a song's share of the list is its chance.
            float totalEntries = SongChoosingList.Count;
            foreach (var songId in chances.Keys.ToArray())
                chances[songId] /= totalEntries;
        }

        return chances;
    }

    float GetTargetSongChoosingAmount(UpvotedSong curSong, IReadOnlyList<AvailableSong> AvailableSongs)
    {
        float amount = 1;
        float ChanceIncreasePerUpvote = 1000f / AvailableSongs.Count;
        if (curSong != null)
        {
            switch (0) // Im keeping old choosing algorithms so I can experiment
            {
                case 0: // Default choosing
                        // Give songs with good ratio extra chance
                    float ratio = 0;
                    if (curSong.TotalDislikes > 0)
                        ratio = curSong.TotalLikes / (float)curSong.TotalDislikes;
                    else if (curSong.TotalLikes > 0)
                        ratio = float.MaxValue;
                    amount += (HelperFuncs.Sigmoid(ratio) - 0.5f) * 100 * ChanceIncreasePerUpvote;

                    // Give songs with good score extra chance
                    if (curSong.Score > 0)
                        amount += (int)Math.Ceiling(curSong.Score * ChanceIncreasePerUpvote);

                    // Give young songs extra chance
                    double age = (DateTime.Now - curSong.DateAdded)?.TotalDays ?? double.MaxValue;
                    if (age < 30)
                        amount += (int)((30 - age) * ChanceIncreasePerUpvote * 60f / 30f);
                    break;
            }
        }
        if (amount < 1)
            amount = 1;
        return amount;
    }

    void TestChoosingListIntegrity(IReadOnlyList<AvailableSong> AvailableSongs)
    {
        // Both sides are computed in one pass each instead of per song:
        // - the entry count of every song comes from a single counting pass over the choosing list,
        //   instead of scanning the whole list for every song. The list holds one entry per chance unit
        //   (~76k entries for this library), and AvailableSong is a record, so the old
        //   "SongChoosingList.FindAll(x => x == song)" compared every entry by value: ~2.6e8 string/Guid
        //   comparisons and a multi-second UI freeze on EVERY vote (this check runs from
        //   UpdateSongChoosingDataStructure) and on every chooser rebuild.
        // - the rows are read with ONE query instead of one EF lookup per song (~3.5k queries per call).
        // The checked values are the same, so the check still catches the same inconsistencies.
        using var dbContext = DbWrapper.GetContext();
        var entryCounts = new Dictionary<AvailableSong, int>();
        foreach (var entry in SongChoosingList)
            entryCounts[entry] = entryCounts.TryGetValue(entry, out int alreadyCounted) ? alreadyCounted + 1 : 1;

        var rowsBySongId = new Dictionary<Guid, UpvotedSong>();
        foreach (var row in dbContext.DumpUpvotedSongs())
            if (row.SongId != Guid.Empty)
                rowsBySongId[row.SongId] = row;

        foreach (var availableSong in AvailableSongs)
        {
            float count = entryCounts.TryGetValue(availableSong, out int counted) ? counted : 0;
            var upvotedSong = availableSong.UpvotedSongId is Guid songId && rowsBySongId.TryGetValue(songId, out var row)
                ? row
                : throw new InvalidDataException($"SongId {availableSong.UpvotedSongId} not found!");
            float target = GetTargetSongChoosingAmount(upvotedSong, AvailableSongs) + 1;

            if (Math.Abs(count - target) > 2)
                availableSong.GetHashCode(); // Breakpoint here
        }
    }
}