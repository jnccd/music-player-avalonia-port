using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayerAvaloniaPort.Helpers;
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

        // Picking a color in the options window replaces the brushes, which the view has to be told about
        // so its bindings below are re-evaluated (repainting the bars with the new color).
        ThemeColors.PrimaryColorChanged += () =>
        {
            OnPropertyChanged(nameof(PrimaryBrush));
            OnPropertyChanged(nameof(PrimaryBrighterBrush));
        };
    }

    // --- Properties ---

    /// <summary>
    /// The accent color of the main view (see <see cref="ThemeColors"/>): the bars and the progress bar
    /// bind to these instead of to static XAML resources, so a color picked in the options window shows up
    /// immediately. The hand-rendered parts (diagram, progress bar) read the same brushes directly.
    /// </summary>
    public IBrush PrimaryBrush => ThemeColors.PrimaryBrush;
    public IBrush PrimaryBrighterBrush => ThemeColors.PrimaryBrighterBrush;

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
