using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Path = Avalonia.Controls.Shapes.Path;

namespace MusicPlayerAvaloniaPort;

public partial class MainView : UserControl
{
    Rectangle? durationBarBackRectangle;
    Rectangle? durationBarAntiAliasingRectangle;
    Rectangle? durationBarDurationRectangle;
    SolidColorBrush? AntiAliasRectBrush;
    double PixelSize = 0.5;

    private void InitPlayProgressUpdater()
    {
        durationBarBackRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarBackRectangle")!;
        durationBarAntiAliasingRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarAntiAliasingRectangle")!;
        durationBarDurationRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarDurationRectangle")!;

        var primaryColorBrush = this.FindResource("PrimaryColor") as SolidColorBrush;
        AntiAliasRectBrush = new SolidColorBrush(primaryColorBrush!.Color, primaryColorBrush.Opacity);
        durationBarAntiAliasingRectangle.Fill = AntiAliasRectBrush;

        PixelSize = (this.GetVisualRoot() as Window)!.RenderScaling;
    }

    private void DoPlayProgressUpdate()
    {
        var window = this.GetVisualRoot() as Window;

        var pixelProgress = durationBarBackRectangle?.Bounds.Width * audioLibWrapper.PlayProgress ?? 0;
        durationBarDurationRectangle?.Width = pixelProgress - PixelSize > 0 ? pixelProgress - PixelSize : 0;
        durationBarAntiAliasingRectangle?.Width = pixelProgress;
        AntiAliasRectBrush?.Opacity = pixelProgress % PixelSize;
    }
}