using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Views.Main;

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
        else if (ApplicationLifetime == null || ApplicationLifetime.GetType().Name == "DesignTimeApplicationLifetime")
        {
            Console.WriteLine("This seems to be a previewer run! How exciting!");
        }
        else
        {
            File.AppendAllText("error.log", $"ApplicationLifetime {ApplicationLifetime.GetType().Name} start failed!\n");
            throw new NotSupportedException($"ApplicationLifetime {ApplicationLifetime.GetType().Name} start failed!");
        }

        base.OnFrameworkInitializationCompleted();
    }
}