using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayerAvaloniaPort.Configuration;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services;
using static MusicPlayerAvaloniaPort.Views.MainView.UiLoopTitle;
using Path = Avalonia.Controls.Shapes.Path;

namespace MusicPlayerAvaloniaPort.Views.MainView;

public partial class MainView : UserControl
{
    private void VolumeBarStackPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        Debug.WriteLine("VolumeBarStackPanel_PointerMoved!");
        if (!e.Properties.IsLeftButtonPressed)
            return;
        if (sender is not StackPanel eventRoot)
        {
            Debug.WriteLine("eventRoot null?");
            return;
        }

        var clickPoint = e.GetPosition(eventRoot);
        var targetPercentage = (clickPoint.X - 3) / (eventRoot.Bounds.Width - 7); // I love magic numbers
        targetPercentage = double.Clamp(targetPercentage, 0, 1);
        targetPercentage *= MAX_VOLUME;
        audioLibWrapper.Volume = (float)targetPercentage;
        //Console.WriteLine($"Volume set to {audioLibWrapper.Volume}");

        UpdateVolumeUi();

        e.Handled = true;
    }

    private void UpdateVolumeUi()
    {
        var volumeBarUserRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "VolumeBarUserRectangle");
        var volumeBarRealRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "VolumeBarRealRectangle");
        if (volumeBarUserRectangle == null || volumeBarRealRectangle == null)
            return;

        var totalWidth = (this.FindResource("VolumeBarWidth") as double?) ?? 100;
        var volumePercent = audioLibWrapper.Volume / MAX_VOLUME;
        volumeBarUserRectangle.Width = totalWidth * volumePercent;
        volumeBarRealRectangle.Width = totalWidth * volumePercent * 0.8;

        var volumeIconArc1 = this.GetLogicalDescendants().OfType<Path>().FirstOrDefault(x => x.Name == "VolumeIconVolumeArc1Path");
        var volumeIconArc2 = this.GetLogicalDescendants().OfType<Path>().FirstOrDefault(x => x.Name == "VolumeIconVolumeArc2Path");
        var volumeIconArc3 = this.GetLogicalDescendants().OfType<Path>().FirstOrDefault(x => x.Name == "VolumeIconVolumeArc3Path");
        if (volumeIconArc1 == null || volumeIconArc2 == null || volumeIconArc3 == null)
        {
            Console.WriteLine("Volume arcs not found!");
            return;
        }
        volumeIconArc1.IsVisible = audioLibWrapper.Volume > 0.1;
        volumeIconArc2.IsVisible = audioLibWrapper.Volume > 0.5;
        volumeIconArc3.IsVisible = audioLibWrapper.Volume > 0.9;
    }
}