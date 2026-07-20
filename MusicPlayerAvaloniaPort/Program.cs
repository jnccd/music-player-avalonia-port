using Avalonia;
using System;
using System.IO;
using System.Net;

namespace MusicPlayerAvaloniaPort;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        WrapInTry(() =>
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        });
    }

    public static void WrapInTry(Action action, bool EndProgramOnError = true)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            File.AppendAllText("./error.log", $"\n\n---{DateTime.Now}---\n{ex}");

            if (EndProgramOnError)
                Environment.Exit(1);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions())
            .With(new X11PlatformOptions())
            .WithDeveloperTools()
            .LogToTrace();
}
