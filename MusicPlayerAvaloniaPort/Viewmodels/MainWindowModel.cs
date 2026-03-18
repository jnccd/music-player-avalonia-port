using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MusicPlayerAvaloniaPort.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {

    }

    [ObservableProperty]
    private float _volume = 1;
    [ObservableProperty]
    private float _playProgress = 0;

    [ObservableProperty]
    private bool _upvoteLocked = false;
    [RelayCommand]
    public void ToggleUpvoteLocked()
    {
        UpvoteLocked = !UpvoteLocked;
    }
}
