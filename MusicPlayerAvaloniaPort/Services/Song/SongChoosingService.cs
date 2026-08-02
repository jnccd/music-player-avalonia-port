using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;
using System;
using System.Collections.Generic;

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

            SongChoosingList.Clear();
            for (int i = 0; i < AvailableSongs.Count; i++)
            {
                var availableSong = AvailableSongs[i];

                var upvotedSong = dbContext.GetUpvotedSongById(availableSong.UpvotedSongId);

                float amount = GetTargetSongChoosingAmount(upvotedSong, AvailableSongs);
                for (int k = 0; k < amount; k++)
                    SongChoosingList.Add(availableSong);

                CreateSongChoosingDataStructureProgress = (i + 1) / (float)AvailableSongs.Count;
            }

#if DEBUG
            TestChoosingListIntegrity(AvailableSongs);
#endif
        }
    }

    public void UpdateSongChoosingDataStructure(AvailableSong songToUpdateListFor, List<AvailableSong> AvailableSongs)
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

            for (int j = 0; j < amount - count; j++)
                SongChoosingList.Insert(index, songToUpdateListFor);
            for (int j = 0; j < count - amount; j++)
                SongChoosingList.RemoveAt(index);

#if DEBUG
            TestChoosingListIntegrity(AvailableSongs);
#endif
        }
    }

    public float GetSongChoosingChance(AvailableSong? songToCheckChanceFor)
    {
        if (songToCheckChanceFor == null)
            return float.NaN;

        lock (SongChoosingList)
        {
            using var dbContext = DbWrapper.GetContext();

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

    float GetTargetSongChoosingAmount(UpvotedSong curSong, List<AvailableSong> AvailableSongs)
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

    void TestChoosingListIntegrity(List<AvailableSong> AvailableSongs)
    {
        using var dbContext = DbWrapper.GetContext();

        foreach (var availableSong in AvailableSongs)
        {
            float count = SongChoosingList.FindAll(x => x == availableSong).Count;
            var upvotedSong = dbContext.GetUpvotedSongById(availableSong.UpvotedSongId);
            float target = GetTargetSongChoosingAmount(upvotedSong, AvailableSongs) + 1;

            if (Math.Abs(count - target) > 2)
                availableSong.GetHashCode(); // Breakpoint here
        }
    }
}