using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class StatisticsSongViewModel(UpvotedSong Song) : ViewModelBase
{
    // --- Properties ---

    public string SongName => Song.Name;

    // --- Commands ---

    // ...
}
