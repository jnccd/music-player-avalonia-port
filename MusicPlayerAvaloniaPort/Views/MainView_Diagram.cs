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
        var width = canvas.Bounds.Width;
        var height = canvas.Bounds.Height;
        var margin = 10;
        var fftDataSpace = width - margin * 2;

        var path = canvas.Children.OfType<Path>().FirstOrDefault(x => x.Name == "MyPath")!;
        var geometry = new PathGeometry();
        var figure = new PathFigure() { IsClosed = true, IsFilled = true };
        Dispatcher.UIThread.Post(() =>
        {
            path.Data = geometry;
            geometry.Figures?.Add(figure);
            figure.StartPoint = new Point(width - margin, height - margin);
            figure.Segments!.Add(new LineSegment() { Point = new Point(width, height - margin) });
            figure.Segments!.Add(new LineSegment() { Point = new Point(width, height) });
            figure.Segments!.Add(new LineSegment() { Point = new Point(0, height) });
            figure.Segments!.Add(new LineSegment() { Point = new Point(0, height - margin) });
            figure.Segments!.Add(new LineSegment() { Point = new Point(margin, height - margin) });
            for (int i = margin; i < width - margin; i++)
            {
                figure.Segments!.Add(new LineSegment() { Point = new Point(i, height - margin) });
            }
        });

        Task.Run(() =>
        {
            while (true)
            {
                stopwatch.Restart();
                float[] fftData = audioLibWrapper.GetCurrentFftSpectrumData();
                Dispatcher.UIThread.Post(() =>
                {
                    width = canvas.Bounds.Width;
                    height = canvas.Bounds.Height;
                    fftDataSpace = width - margin * 2;
                    for (int i = margin; i < width - margin; i++)
                    {
                        var sampledListVal = fftData[(int)((i - margin) / fftDataSpace * fftData.Length / 4)] / 200 * height;
                        (figure.Segments![i - margin + 5] as LineSegment)!.Point = new Point(i, height - margin - sampledListVal);
                    }
                });

                frameCounter++;
                var frameTime = stopwatch.ElapsedMilliseconds;
                var sleepTime = 16 - (int)frameTime;
                Task.Delay(16).Wait();
            }
        });
    }
}