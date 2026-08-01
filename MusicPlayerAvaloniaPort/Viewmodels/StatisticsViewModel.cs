using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;

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
