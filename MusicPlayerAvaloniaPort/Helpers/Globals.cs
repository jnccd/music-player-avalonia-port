using System;
using Avalonia;

namespace MusicPlayerAvaloniaPort.Helpers;

public static class Globals
{
    public static readonly Point InitialWindowSize = new(625, 375);
    public static readonly Point MinWindowSize = new(300, 150);
    public static readonly bool IsDesktop = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
#if DEBUG
    public static readonly string RunConfig = "Debug";
#else
    public static readonly string RunConfig = "Release";
#endif
}