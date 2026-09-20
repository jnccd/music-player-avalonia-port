using System.Collections.ObjectModel;
using MusicPlayerAvaloniaPort.Services.Infrastructure;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class StatisticsViewModel : ViewModelBase
{
    static DbWrapperService? dbWrapper = ServiceContainer.GetService<DbWrapperService>();

    // --- Properties ---

    public ObservableCollection<StatisticsSongViewModel> StatisticsSongVMs { get; }
        = new([]);

    // --- Commands ---

    // ...
}
