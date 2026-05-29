using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SkiaSharp;
using MusicPlayerAvaloniaPort.Configuration;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Helpers;
using Avalonia.Threading;
using Color = Avalonia.Media.Color;
using MusicPlayerAvaloniaPort.Services;
using MusicPlayerAvaloniaPort.Services.UiUpdateLoop;
using static MusicPlayerAvaloniaPort.Views.MainView.UiLoopDiagram;
using Avalonia.Controls.Shapes;

namespace MusicPlayerAvaloniaPort.Views.MainView;

public partial class MainView : UserControl
{
    Window? window => this.GetVisualRoot() as Window;
    MainViewModel? viewModel => DataContext as MainViewModel;
    OptionsView.OptionsView OptionsView = new();

    SongManagerService songManager = ServiceContainer.GetService<SongManagerService>();
    AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    UiUpdateLoopService uiUpdateLoop = ServiceContainer.GetService<UiUpdateLoopService>();

    const double MAX_VOLUME = 1;

    public MainView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        window?.AttachDevTools();
#endif

        // Events
        this.Loaded += MainView_Loaded;
    }

    private void MainView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("MainView loaded!");

        // Setup 
        MapLocalSongLibrary();

        // Events
        window?.Closing += MainView_Closing;
        window?.ScalingChanged += MainView_ScalingChanged;
        songManager.NewSongStarted += (s, song) => UpdateUiForNewSong(song);

        // Ui loops
        uiUpdateLoop.AddInput(new UiLoopDiagram.Input(this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "DiagramCanvas")!));
        uiUpdateLoop.AddInput(new UiLoopPlayProgress.Input(
            this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarBackRectangle")!,
            this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarAntiAliasingRectangle")!,
            this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarDurationRectangle")!,
            this.FindResource("PrimaryColor") as SolidColorBrush,
            PixelScale: 1 / (this.GetVisualRoot() as Window)!.RenderScaling));
        uiUpdateLoop.AddInput(new UiLoopTitle.Input(this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "TitleCanvas")!));

        uiUpdateLoop.Init();
        uiUpdateLoop.StartLoopThread();

        // Initial Update
        MainView_ScalingChanged(null, EventArgs.Empty);
        songManager.GetNextSong();
        LoadVolume();
    }

    void ButtonOptions_Click(object? sender, RoutedEventArgs e)
    {
        var optionsWIndow = AvaloniaWindowManager.GetWindow(typeof(OptionsView.OptionsView));
        optionsWIndow.Show();
        optionsWIndow.Focus();
    }

    private void MainView_Closing(object? sender, WindowClosingEventArgs e)
    {
        Config.Save();
        Debug.WriteLine("MainView closing!");
    }

    private void MainView_ScalingChanged(object? sender, EventArgs e)
    {
        // Fix Shadow Scaling
        var rootGrid = this.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(x => x.Name == "RootGrid")!;
        var effect = rootGrid.Effect as DropShadowEffect;
        effect?.OffsetX = 5 * (this.VisualRoot?.RenderScaling ?? 1);
        effect?.OffsetY = 5 * (this.VisualRoot?.RenderScaling ?? 1);
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