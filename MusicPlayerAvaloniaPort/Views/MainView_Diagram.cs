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
        var valList = new List<float>();
        var curVal = 30f;
        var curValPrime = 0.0f;
        for (int i = 0; i < canvas.Width; i++)
        {
            curValPrime += curVal switch
            {
                < 1 => Random.Shared.Next(1, 2),
                <= 4 => Random.Shared.Next(-1, 2),
                > 4 => Random.Shared.Next(-1, 1),
                _ => 0
            };
            curVal += curValPrime;
            valList.Add(curVal);
        }
        Task.Run(() =>
        {
            while (true)
            {
                stopwatch.Restart();
                Dispatcher.UIThread.Post(() =>
                {
                    width = canvas.Bounds.Width;
                    height = canvas.Bounds.Height;
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(new Point(width - 10, height - 10), true);
                        context.LineTo(new Point(width, height - 10));
                        context.LineTo(new Point(width, height));
                        context.LineTo(new Point(0, height));
                        context.LineTo(new Point(0, height - 10));
                        context.LineTo(new Point(10, height - 10));
                        for (int i = 10; i < width - 10; i++)
                        {
                            var sampledListVal = i - 10 < valList.Count ? valList[i - 10] : 0;
                            context.LineTo(new Point(i, height - 10 - sampledListVal));
                        }
                        context.EndFigure(true);
                    }
                    curValPrime += curVal switch
                    {
                        < 1 => Random.Shared.Next(1, 2),
                        <= 4 => Random.Shared.Next(-1, 3),
                        > 4 => Random.Shared.Next(-1, 1),
                        _ => 0
                    };
                    curValPrime *= 0.99f;
                    curVal += curValPrime;
                    if (canvas.Bounds.Width >= valList.Count)
                        valList.Add(curVal);
                    if (canvas.Bounds.Width <= valList.Count)
                        valList.RemoveAt(0);

                    path.Data = geometry;
                });

                frameCounter++;
                var sleepTime = 16 - (int)stopwatch.ElapsedMilliseconds;
                Task.Delay(sleepTime > 0 ? sleepTime : 0).Wait();
            }
        });
    }
}