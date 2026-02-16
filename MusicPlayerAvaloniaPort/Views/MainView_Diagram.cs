using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace MusicPlayerAvaloniaPort;

public partial class MainView : UserControl
{
    void RunDiagramUpdater()
    {
        int frameCounter = 0;
        Stopwatch stopwatch = new Stopwatch();

        var canvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "DiagramCanvas")!;
        var path = canvas.Children.OfType<Path>().FirstOrDefault(x => x.Name == "MyPath")!;
        var width = canvas.Bounds.Width;
        var height = canvas.Bounds.Height;
        var margin = 10;

        Task.Run(() =>
        {
            bool dispatchComplete = false;
            while (true)
            {
                stopwatch.Restart();
                dispatchComplete = false;
                float[] fftData = audioLibWrapper.GetCurrentFftSpectrumData();
                Dispatcher.UIThread.Post(() =>
                {
                    width = canvas.Bounds.Width;
                    height = canvas.Bounds.Height;
                    var fftDataSpace = width - margin * 2;
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(new Point(width - margin, height - margin), true);
                        context.LineTo(new Point(width, height - margin));
                        context.LineTo(new Point(width, height));
                        context.LineTo(new Point(0, height));
                        context.LineTo(new Point(0, height - margin));
                        context.LineTo(new Point(10, height - margin));
                        for (int i = margin; i < width - margin; i++)
                        {
                            var sampledListVal = fftData[(int)((i - margin) / fftDataSpace * fftData.Length / 4)] / 200 * height;
                            context.LineTo(new Point(i, height - margin - sampledListVal));
                        }
                        context.EndFigure(true);
                    }

                    path.Data = geometry;
                    dispatchComplete = true;
                });
                while (!dispatchComplete) { Task.Delay(1).Wait(); }

                frameCounter++;
                var frameTime = stopwatch.ElapsedMilliseconds;
                var sleepTime = 16 - (int)frameTime;
                Task.Delay(sleepTime > 0 ? sleepTime : 0).Wait();
            }
        });
    }
}