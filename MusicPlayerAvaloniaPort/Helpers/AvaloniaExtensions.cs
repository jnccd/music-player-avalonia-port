using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort.Helpers;

public static class AvaloniaExtensions
{
    public static TControl GetNestedControl<TControl>(this UserControl parentView, string controlName) where TControl : Control
    {
        return parentView
            .GetLogicalDescendants()
            .OfType<TControl>()
            .FirstOrDefault(x => x.Name == controlName)
            ?? throw new Exception($"{controlName} is not in {parentView.Name}");
    }

    record OpenUrlActionOnSystem(bool IsCurrentOperatingSystem, Action<string> OpenUrl);
    static List<OpenUrlActionOnSystem> OpenUrlActionsOnSystem { get; set; } = [
        new(OperatingSystem.IsWindows(), (url) =>
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            })),
        new(OperatingSystem.IsLinux(), (url) =>
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = url,
                UseShellExecute = true
            }))
    ];
    public static void OpenUrlOnCurrentOsBrowser(this Window window, string url)
    {
        var action = OpenUrlActionsOnSystem.FirstOrDefault(x => x.IsCurrentOperatingSystem);

        if (action != null)
        {
            action.OpenUrl(url);
        }
        else
        {
            new MessageBox(e => Console.WriteLine(e), window, window)
                .Show("Platform not supported", "This platform cant show links :(\nPlease open " + url);
        }
    }
}