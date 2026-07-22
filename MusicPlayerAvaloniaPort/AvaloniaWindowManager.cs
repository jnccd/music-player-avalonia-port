using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Views.Main;
using MusicPlayerAvaloniaPort.Views.Options;
using MusicPlayerAvaloniaPort.Views.Statistics;

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

    // In C# its best to define objects like the Dictionary here in chunks, otherwise the compiler errors will be very inaccurate
    static Func<Window> MainWindowCreator = () => new Window
    {
        Content = new MainView
        {
            DataContext = new MainViewModel()
        },
        Position = Config.Data.WindowPositionX != null && Config.Data.WindowPositionY != null ? new PixelPoint(Config.Data.WindowPositionX.Value, Config.Data.WindowPositionY.Value) : new PixelPoint(100, 100),
        Width = Config.Data.Width ?? Globals.InitialWindowSize.X,
        Height = Config.Data.Height ?? Globals.InitialWindowSize.Y,
        ShowInTaskbar = true,
        Title = "MusicPlayer",
        Icon = new WindowIcon("./Assets/icon.ico"),
        Background = new SolidColorBrush(Color.Parse("#1a000000")),
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent],
        ExtendClientAreaToDecorationsHint = true,
        ExtendClientAreaTitleBarHeightHint = -1,
        WindowDecorations = OperatingSystem.IsLinux() ? WindowDecorations.None : WindowDecorations.BorderOnly,
    };
    static Func<Window> OptionsWindowCreator = () => new Window
    {
        Content = new OptionsView
        {
            DataContext = new OptionsViewModel()
        },
        Title = "MusicPlayer Options",
        Icon = new WindowIcon("./Assets/icon.ico"),
        SizeToContent = SizeToContent.WidthAndHeight
    };
    static Func<Window> StatisticsWindowCreator = () => new Window
    {
        Content = new StatisticsView
        {
            DataContext = new StatisticsViewModel()
        },
        Title = "MusicPlayer Statistics",
        Icon = new WindowIcon("./Assets/icon.ico")
    };

    static readonly Dictionary<Type, (Window? window, Func<Window> createWindow)> Windows = new()
    {
        {
            typeof(MainView),
            (null, MainWindowCreator)
        },
        {
            typeof(OptionsView),
            (null, OptionsWindowCreator)
        },
        {
            typeof(StatisticsView),
            (null, StatisticsWindowCreator)
        }
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

    public static void ShowWindow(Type viewType)
    {
        var window = GetWindow(viewType);
        window.Show();
        window.Focus(Avalonia.Input.NavigationMethod.Pointer);
        window.BringIntoView();
        window.Activate();
    }
}