using System;
using System.Diagnostics;
using System.IO;

namespace MusicPlayerAvaloniaPort.Helpers;

/// <summary>
/// Cross-platform "shell" helpers: opening files/URLs with the OS default application and revealing a
/// file in its folder / file manager (Explorer select on Windows, Finder on macOS, Dolphin/Nautilus/
/// xdg-open on Linux).
/// </summary>
public static class PlatformShell
{
    /// <summary>
    /// Opens a file or URL with the OS default application (explorer/shell association on Windows,
    /// xdg-open elsewhere). Returns false when nothing could be started.
    /// </summary>
    public static bool OpenWithDefaultApplication(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else
            {
                Process.Start("xdg-open", target);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reveals a file in its folder and selects it:
    /// - Windows: explorer.exe "/select,&lt;path&gt;" (no space after the comma - with one, Explorer
    ///   tends to just open its default page instead of selecting the file).
    /// - macOS: "open -R".
    /// - Linux: reveals in the file manager of the running desktop (Dolphin "--select" on KDE,
    ///   Nautilus "--select" on GNOME), with xdg-open on the containing folder as last resort.
    /// Returns false when the file does not exist or nothing could be started.
    /// </summary>
    public static bool RevealFileInFileManager(string filePath)
    {
        // The library root can be stored with forward slashes (e.g. "N:/Media/Music"), which Directory
        // enumeration then mixes with backslash-joined names ("N:/Media/Music\1 Downloads\song.mp3").
        // Explorer's /select only works with a clean platform path, so normalize first.
        filePath = Path.GetFullPath(filePath);

        if (!File.Exists(filePath))
            return false;

        string? directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // explorer.exe is special: parameterized launches only work through ShellExecuteEx
                // (UseShellExecute = true). With plain CreateProcess (.NET's default for
                // Process.Start(fileName, args)) Explorer ignores /select and just opens its
                // default page. The comma directly follows /select - with a space in between
                // Explorer also fails to select the file.
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
                return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", $"-R \"{filePath}\"");
                return true;
            }

            // Linux: prefer the file manager of the desktop that is actually running.
            string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            string session = Environment.GetEnvironmentVariable("DESKTOP_SESSION") ?? "";
            bool isKdeSession = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KDE_FULL_SESSION"))
                || desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase)
                || session.Contains("plasma", StringComparison.OrdinalIgnoreCase);
            bool isGnomeSession = desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase)
                || session.Contains("gnome", StringComparison.OrdinalIgnoreCase);
            bool isXfceSession = desktop.Contains("XFCE", StringComparison.OrdinalIgnoreCase);

            if (ExecutableOnPath("dolphin") && (isKdeSession || !isGnomeSession))
                return StartReveal("dolphin", $"--select \"{filePath}\"", directory);
            if (ExecutableOnPath("nautilus") && (isGnomeSession || !isKdeSession))
                return StartReveal("nautilus", $"--select \"{filePath}\"", directory);
            if (isXfceSession && ExecutableOnPath("thunar"))
                return StartReveal("thunar", $"\"{directory}\"", directory); // thunar has no select

            // Desktop unknown or its file manager is missing: try the common ones in order.
            if (ExecutableOnPath("dolphin"))
                return StartReveal("dolphin", $"--select \"{filePath}\"", directory);
            if (ExecutableOnPath("nautilus"))
                return StartReveal("nautilus", $"--select \"{filePath}\"", directory);

            // Last resort: open the containing folder with the default handler.
            return OpenWithDefaultApplication(directory);
        }
        catch
        {
            return false;
        }
    }

    static bool StartReveal(string tool, string arguments, string fallbackDirectory)
    {
        try
        {
            Process.Start(new ProcessStartInfo(tool)
            {
                Arguments = arguments,
                UseShellExecute = false
            });
            return true;
        }
        catch
        {
            // The executable is there but could not be launched: at least open the folder.
            return OpenWithDefaultApplication(fallbackDirectory);
        }
    }

    /// <summary>
    /// Whether an executable of the given name is available through PATH.
    /// </summary>
    public static bool ExecutableOnPath(string fileName)
    {
        try
        {
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathVariable))
                return false;

            string separator = OperatingSystem.IsWindows() ? ";" : ":";
            foreach (string directory in pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, fileName)))
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }
}
