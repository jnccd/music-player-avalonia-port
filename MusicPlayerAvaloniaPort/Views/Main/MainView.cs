using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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

        // Initial Update
        MainView_ScalingChanged(null, EventArgs.Empty);
        LoadVolume();
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
        // Fix Shadow Scaling
        // var rootGrid = this.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(x => x.Name == "RootGrid")!;
        // var effect = rootGrid.Effect as DropShadowEffect;
        // effect?.OffsetX = 5 * (window?.RenderScaling ?? 1);
        // effect?.OffsetY = 5 * (window?.RenderScaling ?? 1);
    }

    private void MainView_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var diagramControl = this.GetLogicalDescendants().OfType<CustomRenderControl_Diagram>().FirstOrDefault(x => x.Name == "CustomRenderControl_Diagram");
        diagramControl!.UpdateDiagramScaling();
    }

    void ButtonClose_Click(object? sender, RoutedEventArgs e)
    {
        window?.Close();
        Environment.Exit(0);
    }
}