using System.Collections.Generic;
using MusicPlayerAvaloniaPort.Helpers.Export;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class ExportLibraryViewModel : ViewModelBase
{
    // --- Properties ---

    List<ExportCandidate> selectedSongs = [];
    /// <summary>
    /// The songs that pass the thresholds of the view (best first) - exactly what the export copies.
    /// Replaced as a whole whenever the selection changes: the DataGrid then re-reads one list instead of
    /// being told about thousands of single row changes while a threshold slider is dragged.
    /// </summary>
    public List<ExportCandidate> SelectedSongs
    {
        get => selectedSongs;
        set
        {
            selectedSongs = value;
            OnPropertyChanged();
        }
    }

    // --- Commands ---

    // ...
}
