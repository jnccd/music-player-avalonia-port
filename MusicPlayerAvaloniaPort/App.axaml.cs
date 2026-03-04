using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Configuration;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.ViewModels;

namespace MusicPlayerAvaloniaPort;

public partial class App : Application
{
    public MainViewModel MainViewModel { get; private set; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Content = new MainView
                {
                    DataContext = MainViewModel
                },
                Position = Config.Data.Pos ?? new PixelPoint(100, 100),
                Width = Config.Data.Width ?? Globals.InitialWindowSize.X,
                Height = Config.Data.Height ?? Globals.InitialWindowSize.Y,
                ShowInTaskbar = true,
                Title = "MusicPlayer",
                Icon = new WindowIcon("./Assets/icon.ico"),
                Background = new SolidColorBrush(Color.Parse("#1a000000")),
                TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent],
                ExtendClientAreaToDecorationsHint = true,
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                ExtendClientAreaTitleBarHeightHint = -1,
            };
            ViewModelBase.MainView = desktop.MainWindow.Content as MainView;
        }
        else
        {
            File.AppendAllText("error.log", "ApplicationLifetime start failed!\n");
            throw new NotSupportedException("ApplicationLifetime start failed!");
        }

        base.OnFrameworkInitializationCompleted();
    }
}