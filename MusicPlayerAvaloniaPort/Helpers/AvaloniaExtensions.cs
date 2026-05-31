using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System;
using System.Collections.Generic;
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
}