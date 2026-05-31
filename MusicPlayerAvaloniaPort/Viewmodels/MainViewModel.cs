using System.Dynamic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services;

namespace MusicPlayerAvaloniaPort.ViewModels;
#pragma warning disable CS9266 // Property accessor should use 'field' because the other accessor is using it.

public partial class MainViewModel : ViewModelBase
{
    AudioLibWrapperService? audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();

    // --- Properties ---

    // Playback
    public float VolumeMultiplier
    {
        get => audioLibWrapper?.Volume ?? 0;
        set
        {
            audioLibWrapper?.Volume = value;
            field = value;
            SetProperty(ref field, value);
        }
    } = 0;
    [ObservableProperty]
    private bool _playing = true;

    // Upvote
    [ObservableProperty]
    private bool _upvoteLocked = false;
    [RelayCommand]
    public void ToggleUpvoteLocked()
    {
        UpvoteLocked = !UpvoteLocked;
    }

    // --- Commands ---

    [RelayCommand]
    public void PlayPause()
    {
        audioLibWrapper?.TogglePlayPause();
        Playing = audioLibWrapper?.PlayState == SoundFlow.Enums.PlaybackState.Playing;
    }
}
