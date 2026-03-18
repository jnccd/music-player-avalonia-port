using System;
using System.Diagnostics;
using System.Linq;
using System.Resources;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace MusicPlayerAvaloniaPort;

public partial class MainView : UserControl
{
    double titleLastPlainTitleTime = -1;
    double titleInverseOpacityMaskAnimSpeed = 0.6;
    double titleGap = 60;
    Canvas? titleCanvas = null;
    LinearGradientBrush? titleInitialOpacityMask = null;
    TextBlock? titleCanvasText1 = null;
    TextBlock? titleCanvasText2 = null;
    TimeSpan? titleLastUpdateTime = null;
    double? titleCanvasText1X;
    double? titleCanvasText2X;
    double? titleCanvasText1Width;

    void InitTitleUpdater()
    {
        Stopwatch stopwatch = new();
        titleLastUpdateTime = globalStopwatch.Elapsed;

        titleCanvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "TitleCanvas")!;
        titleInitialOpacityMask = titleCanvas.OpacityMask as LinearGradientBrush;
        titleCanvasText1 = titleCanvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText1")!;
        titleCanvasText2 = titleCanvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText2")!;

        // Needs to be invalidated when the title changes
        titleCanvasText1.Measure(Size.Infinity);
        titleCanvasText1Width = titleCanvasText1.DesiredSize.Width;

        titleCanvasText1X = titleCanvas.Bounds.Width * ((titleInitialOpacityMask?.GradientStops.Skip(1).FirstOrDefault()?.Offset ?? 0.2) / 2);
        titleCanvasText2X = titleCanvasText1X + titleCanvasText1Width + titleGap;
    }
    void DoTitleUpdate(int frameCount)
    {
        var currentTIme = globalStopwatch.Elapsed;
        var movement = (currentTIme! - titleLastUpdateTime!).Value.Milliseconds / 69.0;

        if (titleCanvas!.Bounds.Width > titleCanvasText1!.DesiredSize.Width)
        {
            titleCanvas.OpacityMask = null;
            titleCanvasText1X = 0;
            titleCanvasText2X = -9999;

            titleLastPlainTitleTime = frameCount;
        }
        else
        {
            titleCanvasText1X -= movement;
            titleCanvasText2X = titleCanvasText1X + titleCanvasText1Width + titleGap;
            if (titleCanvasText1X + titleCanvasText1Width < 0)
            {
                titleCanvasText1X = titleCanvasText2X;
                titleCanvasText2X = titleCanvasText1X + titleCanvasText1Width + titleGap;
            }

            titleCanvas.OpacityMask = titleInitialOpacityMask;
            var timeSinceLastPlainTitle = frameCount - titleLastPlainTitleTime;
            if (timeSinceLastPlainTitle < 300)
            {
                titleInitialOpacityMask?.StartPoint = new RelativePoint(titleInverseOpacityMaskAnimSpeed / -timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
                titleInitialOpacityMask?.EndPoint = new RelativePoint(1 + titleInverseOpacityMaskAnimSpeed / timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
            }
        }

        Canvas.SetLeft(titleCanvasText1, titleCanvasText1X!.Value);
        Canvas.SetLeft(titleCanvasText2!, titleCanvasText2X!.Value);

        titleLastUpdateTime = globalStopwatch.Elapsed;
    }
    void ChangeTitle(string newTitle)
    {
        var titleCanvasText1 = this.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText1")!;
        var titleCanvasText2 = this.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText2")!;

        titleCanvasText1.Text = newTitle;
        titleCanvasText2.Text = newTitle;
    }
}