using System.Collections.ObjectModel;
using System.Linq;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class StatisticsViewModel : ViewModelBase
{
    DbWrapperService? dbWrapper = ServiceContainer.GetService<DbWrapperService>();

    // --- Properties ---

    public StatisticsSongViewModel[] StatisticsSongVMs => dbWrapper?
            .GetContext()
            .DumpUpvotedSongs()
            .Select(song => new StatisticsSongViewModel(song))
            .ToArray()
        ?? [];

    // --- Commands ---

    // ...
}
