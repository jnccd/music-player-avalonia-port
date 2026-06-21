using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.UiUpdateLoop;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Views.Options;
using MusicPlayerAvaloniaPort.Views.Statistics;
using static MusicPlayerAvaloniaPort.Views.Main.UiLoopDiagram;

namespace MusicPlayerAvaloniaPort.Views.Main;

public partial class MainView : UserControl
{
    Window? window => TopLevel.GetTopLevel(this) as Window;
    MainViewModel? viewModel => DataContext as MainViewModel;

    SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    UiUpdateLoopService uiUpdateLoop = ServiceContainer.GetService<UiUpdateLoopService>();
    MprisService mprisService = ServiceContainer.GetService<MprisService>();

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
        // Setup 
        MapLocalSongLibrary();

        // Events
        window?.Closing += MainView_Closing;
        window?.ScalingChanged += MainView_ScalingChanged;
        songPlaybackService.NewSongStarted += (s, song) => UpdateUiForNewSong(song);
        songPlaybackService.UpvoteLockedInChanged += (s, lockedIn) => UpdateUiForNewUpvoteLockedInState(lockedIn);

        // Ui loops
        uiUpdateLoop.AddInput(new UiLoopDiagram.Input(this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "DiagramCanvas")!));
        uiUpdateLoop.AddInput(new UiLoopPlayProgress.Input(
            this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarBackRectangle")!,
            this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarAntiAliasingRectangle")!,
            this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarDurationRectangle")!,
            this.FindResource("PrimaryColor") as SolidColorBrush,
            PixelScale: 1 / window!.RenderScaling));
        uiUpdateLoop.AddInput(new UiLoopTitle.Input(this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "TitleCanvas")!));

        uiUpdateLoop.Init();
        uiUpdateLoop.StartLoopThread();

        // Initial Update
        MainView_ScalingChanged(null, EventArgs.Empty);
        songPlaybackService.GetNextSong();
        LoadVolume();

        mprisService.Init();
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
        uiUpdateLoop.InvokeEvent(new UpdateDiagramScalingEventArgs());
    }

    void ButtonClose_Click(object? sender, RoutedEventArgs e)
    {
        window?.Close();
        Environment.Exit(0);
    }
}