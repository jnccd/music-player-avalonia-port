using System.Collections.ObjectModel;
using System.Linq;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class StatisticsViewModel : ViewModelBase
{
    static DbWrapperService? dbWrapper = ServiceContainer.GetService<DbWrapperService>();

    // --- Properties ---

    public ObservableCollection<StatisticsSongViewModel> StatisticsSongVMs
    {
        get; set;
    } = new(
        dbWrapper?
            .GetContext()
            .DumpUpvotedSongs()
            .OrderByDescending(song => song.Score)
            .Select(song => new StatisticsSongViewModel(song))
            .ToArray()
        ?? []
    );

    // --- Commands ---

    // ...
}
