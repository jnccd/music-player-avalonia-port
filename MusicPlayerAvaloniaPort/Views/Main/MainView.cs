using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Views.Options;
using MusicPlayerAvaloniaPort.Views.Statistics;

namespace MusicPlayerAvaloniaPort.Views.Main;

public partial class MainView : UserControl
{
    Window? window => TopLevel.GetTopLevel(this) as Window;
    MainViewModel? viewModel => DataContext as MainViewModel;

    SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    SongVolumeService songVolumeService = ServiceContainer.GetService<SongVolumeService>();
    AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    MprisService? mprisService = ServiceContainer.TryGetService<MprisService>();

    const double MAX_VOLUME = 1;

    CustomRenderControl_Diagram customRenderControl_Diagram => this.GetLogicalDescendants().OfType<CustomRenderControl_Diagram>().FirstOrDefault()!;
    CustomRenderControl_PlayProgress customRenderControl_PlayProgress => this.GetLogicalDescendants().OfType<CustomRenderControl_PlayProgress>().FirstOrDefault()!;
    CustomRenderControl_Title customRenderControl_Title => this.GetLogicalDescendants().OfType<CustomRenderControl_Title>().FirstOrDefault()!;

    public MainView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);

        // Events
        this.Loaded += MainView_Loaded;
    }

    private void MainView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("MainView loaded!");

        SetupUi();
    }

    void SetupUi()
    {
        // Song Setup Thread (so it doesnt block the UI)
        Task.Run(() =>
        {
            Thread.CurrentThread.Name = "SongSetupThread";

            MapLocalSongLibrary();
            songPlaybackService.GetNextSong();

            mprisService?.Init();
        });

        // Events
        window?.Closing += MainView_Closing;
        window?.ScalingChanged += MainView_ScalingChanged;
        songPlaybackService.NewSongStarted += (s, song) => UpdateUiForNewSong(song);
        songPlaybackService.UpvoteLockedInChanged += (s, lockedIn) => UpdateUiForNewUpvoteLockedInState(lockedIn);
        this.AddHandler(
            InputElement.KeyDownEvent,
            UserControl_KeyDown,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        audioLibWrapper.PlaybackStateChanged += (e, s) =>
        {
            RefreshCustomControls();
            customRenderControl_Diagram.UpdateDiagramScaling();
        };

        // Inits
        MainView_ScalingChanged(null, EventArgs.Empty);
        LoadVolume();

        // Visuals
        // On Windows disable the extra border resizing feature since the OS border is active (see AvaloniaWindowManager.cs)
        if (OperatingSystem.IsWindows())
        {
            var cosmeticBorder = this.GetLogicalDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "CosmeticBorder");
            cosmeticBorder?.BorderBrush = new SolidColorBrush(Color.Parse("#00000000"));

            var resizeWindowBorder = this.GetLogicalDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "ResizeWindowBorder");
            resizeWindowBorder?.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
        }
    }

    void RefreshCustomControls()
    {
        Dispatcher.UIThread.InvokeAsync(customRenderControl_Diagram.InvalidateVisual, DispatcherPriority.Background);
        Dispatcher.UIThread.InvokeAsync(customRenderControl_PlayProgress.InvalidateVisual, DispatcherPriority.Background);
        Dispatcher.UIThread.InvokeAsync(customRenderControl_Title.InvalidateVisual, DispatcherPriority.Background);
    }

    void ButtonOptions_Click(object? sender, RoutedEventArgs e)
    {
        AvaloniaWindowManager.ShowWindow(typeof(OptionsView));
    }

    void ButtonStatistics_Click(object? sender, RoutedEventArgs e)
    {
        AvaloniaWindowManager.ShowWindow(typeof(StatisticsView));
    }

    private void MainView_Closing(object? sender, WindowClosingEventArgs e)
    {
        Config.Save();
        Debug.WriteLine("MainView closing!");
    }

    private void MainView_ScalingChanged(object? sender, EventArgs e)
    {

    }

    private void MainView_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        customRenderControl_Diagram.UpdateDiagramScaling();

        RefreshCustomControls();
    }

    void ButtonClose_Click(object? sender, RoutedEventArgs e)
    {
        window?.Close();
        Environment.Exit(0);
    }

    private void UserControl_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V)
        {
            customRenderControl_Diagram.CycleVisMode();
        }
    }
}