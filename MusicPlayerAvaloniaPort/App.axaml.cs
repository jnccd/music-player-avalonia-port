using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Configuration;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Views.MainView;
using MusicPlayerAvaloniaPort.Views.OptionsView;

namespace MusicPlayerAvaloniaPort;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaWindowManager.Initialize(this);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = AvaloniaWindowManager.GetWindow(typeof(MainView));
        }
        else
        {
            File.AppendAllText("error.log", "ApplicationLifetime start failed!\n");
            throw new NotSupportedException("ApplicationLifetime start failed!");
        }

        base.OnFrameworkInitializationCompleted();
    }
}