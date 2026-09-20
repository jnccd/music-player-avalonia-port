using Avalonia;
using MusicPlayerAvaloniaPort.Persistence;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Debug-only command line hook for the wrapped audio analysis self test (see WrappedSelfTest);
        // in a normal run it does nothing at all.
#if DEBUG
        if (Services.Wrapped.WrappedSelfTest.RunIfRequested())
            return;
#endif

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
            WriteErrorLog(ex);

            if (EndProgramOnError)
                Environment.Exit(1);
        }
    }
    public static async Task WrapInTryAsync(Func<Task> action, bool EndProgramOnError = true)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            WriteErrorLog(ex);

            if (EndProgramOnError)
                Environment.Exit(1);
        }
    }

    /// <summary>
    /// Appends a crash report to the error log in the app's data directory (see PersistenceLocations). It
    /// used to be written into the working directory, which for a development install is the project -
    /// and on Linux the build output - folder. This is the last-resort handler while an exception is
    /// being handled, so it must never throw itself.
    /// </summary>
    public static void WriteErrorLog(string message)
    {
        string logPath = "<unresolved>";
        try
        {
            // The path resolution creates the data directory, so it belongs inside the try as well: this
            // runs while another exception is being handled and must never throw.
            logPath = PersistenceLocations.ErrorLogPath;
            File.AppendAllText(logPath, message);
        }
        catch (Exception logException)
        {
            Console.WriteLine($"Could not write the error log ({logPath}): {logException.Message}\nOriginal problem: {message}");
        }
    }

    public static void WriteErrorLog(Exception exception) =>
        WriteErrorLog($"\n\n---{DateTime.Now}---\n{exception}");

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions())
            .With(new X11PlatformOptions())
            .WithDeveloperTools()
            .LogToTrace();
}
