using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.LogicalTree;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using Path = Avalonia.Controls.Shapes.Path;

namespace MusicPlayerAvaloniaPort.Views.Main;

public partial class MainView : UserControl
{
    void LoadVolume()
    {
        audioLibWrapper.Volume = (float)(Config.Data.Volume * MAX_VOLUME);
        UpdateVolumeUi();
    }

    private void VolumeBarStackPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Debug.WriteLine("VolumeBarStackPanel_PointerPressed!");
        VolumeBarStackPanel_PointerDown(sender, e);
    }

    private void VolumeBarStackPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        Debug.WriteLine("VolumeBarStackPanel_PointerMoved!");
        VolumeBarStackPanel_PointerDown(sender, e);
    }

    void VolumeBarStackPanel_PointerDown(object? sender, PointerEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed)
            return;
        if (sender is not StackPanel eventRoot)
        {
            Debug.WriteLine("eventRoot null?");
            return;
        }

        var clickPoint = e.GetPosition(eventRoot);
        var targetPercentage = (clickPoint.X - 3) / (eventRoot.Bounds.Width - 7); // I love magic numbers
        var clampedTargetPercentage = double.Clamp(targetPercentage, 0, 1);
        var targetVolume = clampedTargetPercentage * MAX_VOLUME;
        audioLibWrapper.Volume = (float)targetVolume;
        //Console.WriteLine($"Volume set to {audioLibWrapper.Volume}");

        Config.Data.Volume = clampedTargetPercentage;
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