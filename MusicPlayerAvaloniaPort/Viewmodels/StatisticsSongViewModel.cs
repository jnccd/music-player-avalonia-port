using System;
using System.IO;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class StatisticsSongViewModel(UpvotedSong Song) : ViewModelBase
{
    // --- Properties ---

    public string Name => Path.GetFileNameWithoutExtension(Song.Name);
    public float Score => Song.Score;
    public int Streak => Song.Streak;
    public int Upvotes => Song.TotalLikes;
    public int Downvotes => Song.TotalDislikes;
    public float? VoteRatio => Song.TotalDislikes > 0 ? (float)Song.TotalLikes / Song.TotalDislikes : null;
    public float? Volume => Song.Volume > 0 ? Song.Volume : null;
    public DateTime? DateAdded => Song.DateAdded?.LocalDateTime;
    public float PlayChance => 0;

    // --- Commands ---

    // ...
}
