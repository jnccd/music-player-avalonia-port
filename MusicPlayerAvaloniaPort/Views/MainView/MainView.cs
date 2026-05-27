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
    MainViewModel? viewModel => DataContext as MainViewModel;
    Window? window => this.GetVisualRoot() as Window;

    SongManagerService songManager = ServiceContainer.GetService<SongManagerService>();
    AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    UiUpdateLoopService uiUpdateLoop = ServiceContainer.GetService<UiUpdateLoopService>();

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
#if DEBUG
        window?.AttachDevTools();
#endif
        // Setup 
        MapLocalSongLibrary();

        // Events
        window?.Closing += MainView_Closing;
        window?.ScalingChanged += MainView_ScalingChanged;
        songManager.NewSongStarted += (s, songPath) => UpdateUiForNewSong(songPath);

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
        SetVolumeUi();
    }

    private void MainView_Closing(object? sender, WindowClosingEventArgs e)
    {
        Debug.WriteLine("MainView closing!");
        Config.Save();
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

    private void MainView_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
        {
            songManager.GetNextSong();
        }
        else if (e.Delta.Y < 0)
        {
            songManager.GetPreviousSong();
        }
    }

    void ButtonClose_Click(object? sender, RoutedEventArgs e)
    {
        window?.Close();
    }

    void ButtonUpvote_Click(object? sender, RoutedEventArgs e)
    {
        viewModel?.UpvoteLocked = !viewModel.UpvoteLocked;
        var upvoteButton = sender as Button;

        var path = upvoteButton?.GetLogicalChildren().FirstOrDefault() as Path;
        path?.Fill = viewModel?.UpvoteLocked == true ? this.FindResource("PrimaryColor") as SolidColorBrush : Brushes.White;
    }

    private void VolumeBarStackPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        Debug.WriteLine("VolumeBarStackPanel_PointerMoved!");
        if (!e.Properties.IsLeftButtonPressed)
            return;
        if (sender is not StackPanel eventRoot)
        {
            Debug.WriteLine("eventRoot null?");
            return;
        }

        var clickPoint = e.GetPosition(eventRoot);
        var targetPercentage = (clickPoint.X - 3) / (eventRoot.Bounds.Width - 7); // I love magic numbers
        targetPercentage = double.Clamp(targetPercentage, 0, 1);
        targetPercentage *= MAX_VOLUME;
        audioLibWrapper.Volume = (float)targetPercentage;
        //Console.WriteLine($"Volume set to {audioLibWrapper.Volume}");

        SetVolumeUi();

        e.Handled = true;
    }

    private void SetVolumeUi()
    {
        var volumeBarUserRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "VolumeBarUserRectangle");
        var volumeBarRealRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "VolumeBarRealRectangle");
        if (volumeBarUserRectangle == null || volumeBarRealRectangle == null)
            return;

        var totalWidth = (this.FindResource("VolumeBarWidth") as double?) ?? 100;
        var volumePercent = audioLibWrapper.Volume / MAX_VOLUME;
        volumeBarUserRectangle.Width = totalWidth * volumePercent;
        volumeBarRealRectangle.Width = totalWidth * volumePercent * 0.8;

        var volumeIconArc1 = this.GetLogicalDescendants().OfType<Path>().FirstOrDefault(x => x.Name == "VolumeIconVolumeArc1Path");
        var volumeIconArc2 = this.GetLogicalDescendants().OfType<Path>().FirstOrDefault(x => x.Name == "VolumeIconVolumeArc2Path");
        var volumeIconArc3 = this.GetLogicalDescendants().OfType<Path>().FirstOrDefault(x => x.Name == "VolumeIconVolumeArc3Path");
        if (volumeIconArc1 == null || volumeIconArc2 == null || volumeIconArc3 == null)
        {
            Console.WriteLine("Volume arcs not found!");
            return;
        }
        volumeIconArc1.IsVisible = audioLibWrapper.Volume > 0.1;
        volumeIconArc2.IsVisible = audioLibWrapper.Volume > 0.5;
        volumeIconArc3.IsVisible = audioLibWrapper.Volume > 0.9;
    }

    private void DurationBarStackPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        Debug.WriteLine("DurationBarStackPanel_PointerMoved!");
        if (!e.Properties.IsLeftButtonPressed)
            return;
        if (sender is not StackPanel eventRoot)
        {
            Debug.WriteLine("eventRoot null?");
            return;
        }

        var clickPoint = e.GetPosition(eventRoot);
        var targetPercentage = (clickPoint.X - 3) / (eventRoot.Bounds.Width - 7); // I love magic numbers
        audioLibWrapper.PlayProgress = (float)targetPercentage;

        e.Handled = true;
    }
}