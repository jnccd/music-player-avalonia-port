using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace MusicPlayerAvaloniaPort;

public partial class MainView : UserControl
{
    void ChangeTitle(string newTitle)
    {
        var titleCanvasText1 = this.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText1")!;
        var titleCanvasText2 = this.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText2")!;

        titleCanvasText1.Text = newTitle;
        titleCanvasText2.Text = newTitle;
    }
    void RunTitleUpdater()
    {
        int frameCounter = 0;
        double lastPlainTitleTime = -1;
        Stopwatch stopwatch = new();
        var lastTime = globalStopwatch.Elapsed;
        var inverseOpacityMaskAnimSpeed = 0.6;

        var canvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "TitleCanvas")!;
        var initialOpacityMask = canvas.OpacityMask as LinearGradientBrush;
        var titleCanvasText1 = canvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText1")!;
        var titleCanvasText2 = canvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText2")!;

        // Needs to be invalidated when the title changes
        titleCanvasText1.Measure(Size.Infinity);
        var titleCanvasText1Width = titleCanvasText1.DesiredSize.Width;

        var titleGap = 60;
        var titleCanvasText1X = canvas.Bounds.Width * ((initialOpacityMask?.GradientStops.Skip(1).FirstOrDefault()?.Offset ?? 0.2) / 2);
        var titleCanvasText2X = titleCanvasText1X + titleCanvasText1Width + titleGap;

        Task.Run(() =>
        {
            while (true)
            {
                stopwatch.Restart();
                var currentTIme = globalStopwatch.Elapsed;
                var movement = (currentTIme - lastTime).Milliseconds / 69.0;
                Dispatcher.UIThread.Post(() =>
                {
                    if (canvas.Bounds.Width > titleCanvasText1.DesiredSize.Width)
                    {
                        canvas.OpacityMask = null;
                        titleCanvasText1X = 0;
                        titleCanvasText2X = -9999;

                        lastPlainTitleTime = frameCounter;
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

                        canvas.OpacityMask = initialOpacityMask;
                        var timeSinceLastPlainTitle = frameCounter - lastPlainTitleTime;
                        if (timeSinceLastPlainTitle < 300)
                        {
                            initialOpacityMask?.StartPoint = new RelativePoint(inverseOpacityMaskAnimSpeed / -timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
                            initialOpacityMask?.EndPoint = new RelativePoint(1 + inverseOpacityMaskAnimSpeed / timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
                        }
                    }

                    Canvas.SetLeft(titleCanvasText1, titleCanvasText1X);
                    Canvas.SetLeft(titleCanvasText2, titleCanvasText2X);
                });

                frameCounter++;
                lastTime = globalStopwatch.Elapsed;
                var sleepTime = 14 - (int)stopwatch.ElapsedMilliseconds;
                Task.Delay(sleepTime > 0 ? sleepTime : 0).Wait();
            }
        });
    }
}