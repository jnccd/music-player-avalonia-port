using System.IO;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class StatisticsSongViewModel(UpvotedSong Song) : ViewModelBase
{
    // --- Properties ---

    public string Name => Path.GetFileNameWithoutExtension(Song.Name);
    public float Score => Song.Score;

    // --- Commands ---

    // ...
}
