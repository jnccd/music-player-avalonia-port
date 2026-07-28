using System;
using System.IO;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class StatisticsSongViewModel(UpvotedSong Song) : ViewModelBase
{
    static SongChoosingService? songChoosingService = ServiceContainer.GetService<SongChoosingService>();
    static SongPlaybackService? songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();

    // --- Properties ---

    // For show
    public string Name => Path.GetFileNameWithoutExtension(Song.Name);
    public float Score => Song.Score;
    public int Streak => Song.Streak;
    public int Upvotes => Song.TotalLikes;
    public int Downvotes => Song.TotalDislikes;
    public float? VoteRatio => Song.TotalDislikes > 0 ? (float)Song.TotalLikes / Song.TotalDislikes : null;
    public float? Volume => Song.Volume > 0 ? Song.Volume : null;
    public DateTime? DateAdded => Song.DateAdded?.LocalDateTime;
    public float PlayChance => (float)Math.Round((songChoosingService?.GetSongChoosingChance(songPlaybackService?.FindAvailableSong(Song.SongId)) ?? float.NaN) * 100, 5);

    // For internal
    public Guid SongId = Song.SongId;

    // --- Commands ---

    // ...
}
