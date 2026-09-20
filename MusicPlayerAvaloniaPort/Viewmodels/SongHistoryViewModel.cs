using System.Collections.Generic;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class SongHistoryViewModel : ViewModelBase
{
    // --- Properties ---

    List<SongHistoryItemViewModel> entries = [];
    /// <summary>
    /// The history rows the grid shows (an applied search already filtered them). Replaced as a whole when
    /// the history is reloaded or searched, so the grid re-reads one list instead of thousands of single
    /// row changes.
    /// </summary>
    public List<SongHistoryItemViewModel> Entries
    {
        get => entries;
        set
        {
            entries = value;
            OnPropertyChanged();
        }
    }

    // --- Commands ---

    // ...
}
