using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;

namespace MusicPlayerAvaloniaPort.ViewModels;
#pragma warning disable CS9266 // Property accessor should use 'field' because the other accessor is using it.

public partial class MainViewModel : ViewModelBase
{
    AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    SongVolumeService songVolumeService = ServiceContainer.GetService<SongVolumeService>();

    public MainViewModel()
    {
        audioLibWrapper.PlaybackStateChanged += (e, s) =>
        {
            Playing = s == SoundFlow.Enums.PlaybackState.Playing;
        };
    }

    // --- Properties ---

    // Playback
    public float VolumeMultiplier
    {
        get => songVolumeService.UserDefinedVolume;
        set
        {
            songVolumeService.UserDefinedVolume = value;
            field = value;
            SetProperty(ref field, value);
        }
    } = 0;
    [ObservableProperty]
    private bool _playing = true;

    // Upvote
    [ObservableProperty]
    private bool _upvoteLockedIn = false;

    // --- Commands ---

    [RelayCommand]
    public void PlayPause()
    {
        audioLibWrapper?.TogglePlayPause();
        Playing = audioLibWrapper?.PlayState == SoundFlow.Enums.PlaybackState.Playing;
    }
}
