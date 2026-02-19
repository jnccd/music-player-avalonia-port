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
    int diagramThickness = 10;

    void UpdateDiagramScaling()
    {
        var canvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "DiagramCanvas")!;
        var path = canvas.Children.OfType<Path>().FirstOrDefault(x => x.Name == "MyPath")!;
        var geometry = (path.Data as PathGeometry)!;
        var figure = geometry?.Figures?[0];
        if (figure == null) return;

        var canvasWidth = canvas.Bounds.Width;
        var canvasHeight = canvas.Bounds.Height;
        figure.StartPoint = new Point(canvasWidth, canvasHeight - diagramThickness);
        figure.Segments![0] = new LineSegment() { Point = new Point(canvasWidth, canvasHeight) };
        figure.Segments![1] = new LineSegment() { Point = new Point(0, canvasHeight) };
        figure.Segments![2] = new LineSegment() { Point = new Point(0, canvasHeight - diagramThickness) };
    }

    void RunDiagramUpdater()
    {
        int frameCounter = 0;
        Stopwatch stopwatch = new Stopwatch();

        var canvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "DiagramCanvas")!;
        var canvasWidth = canvas.Bounds.Width;
        var canvasHeight = canvas.Bounds.Height;
        var fftDataSpace = canvasWidth - diagramThickness * 2;

        var path = canvas.Children.OfType<Path>().FirstOrDefault(x => x.Name == "MyPath")!;
        var geometry = new PathGeometry();
        var figure = new PathFigure() { IsClosed = true, IsFilled = true };
        var numBorderSegments = 3;
        Dispatcher.UIThread.Post(() =>
        {
            path.Data = geometry;
            geometry.Figures?.Add(figure);
            figure.StartPoint = new Point(canvasWidth, canvasHeight - diagramThickness);
            figure.Segments!.Add(new LineSegment() { Point = new Point(canvasWidth, canvasHeight) });
            figure.Segments!.Add(new LineSegment() { Point = new Point(0, canvasHeight) });
            figure.Segments!.Add(new LineSegment() { Point = new Point(0, canvasHeight - diagramThickness) });
            for (int i = 0; i < canvasWidth; i++)
            {
                figure.Segments!.Add(new LineSegment() { Point = new Point(i, canvasHeight - diagramThickness) });
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
                    canvasWidth = canvas.Bounds.Width;
                    canvasHeight = canvas.Bounds.Height;
                    fftDataSpace = canvasWidth;
                    while (figure.Segments?.Count - 1 < fftDataSpace + numBorderSegments)
                    {
                        figure.Segments!.Add(new LineSegment() { Point = new Point(diagramThickness + figure.Segments.Count, canvasHeight - diagramThickness) });
                    }
                    while (figure.Segments?.Count - 1 > fftDataSpace + numBorderSegments)
                    {
                        figure.Segments!.RemoveAt(figure.Segments.Count - 1);
                    }
                    for (int i = 0; i < canvasWidth; i++)
                    {
                        var sampledListVal = fftData[(int)(i / fftDataSpace * fftData.Length / 4)] / 200 * canvasHeight;
                        (figure.Segments![i + numBorderSegments] as LineSegment)!.Point = new Point(i, canvasHeight - diagramThickness - sampledListVal);
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