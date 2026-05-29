using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Configuration;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Views.Main;
using MusicPlayerAvaloniaPort.Views.Options;

namespace MusicPlayerAvaloniaPort;

public static class AvaloniaWindowManager
{
    static bool isInit = false;
    static App? app = null;

    public static void Initialize(App initApp)
    {
        if (isInit) return;
        isInit = true;
        app = initApp;
    }

    static readonly Dictionary<Type, (Window? window, Func<Window> createWindow)> Windows = new()
    {
        {
            typeof(MainView),
            (null, () => new Window
            {
                Content = new MainView
                {
                    DataContext = new MainViewModel()
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
            })
        },
        {
            typeof(OptionsView),
            (null, () => new Window
            {
                Content = new OptionsView
                {
                    DataContext = new OptionsViewModel()
                },
                Title = "MusicPlayer Options",
                Icon = new WindowIcon("./Assets/icon.ico"),
            })
        },
    };
    public static Window GetWindow(Type viewType)
    {
        var windowTuple = Windows[viewType];

        var lifetime = (app ?? throw new ArgumentNullException(nameof(app))).ApplicationLifetime;
        var desktopLifetime = lifetime as IClassicDesktopStyleApplicationLifetime;

        if (windowTuple.window == null || desktopLifetime?.Windows.Any(window => window.Content?.GetType() == viewType) == false)
        {
            windowTuple.window = windowTuple.createWindow();
        }

        Windows[viewType] = windowTuple;
        return windowTuple.window;
    }
}