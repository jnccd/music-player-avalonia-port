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
    Canvas? diagramCanvas = null;
    Path? diagramPath = null;
    PathFigure? diagramFigure = null;
    int diagramNumBorderSegments = 3;
    double diagramFftDataSpace = 0;

    void InitDiagramUpdater()
    {
        diagramCanvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "DiagramCanvas")!;
        diagramPath = diagramCanvas.Children.OfType<Path>().FirstOrDefault(x => x.Name == "MyPath")!;

        var canvasWidth = diagramCanvas.Bounds.Width;
        var canvasHeight = diagramCanvas.Bounds.Height;
        diagramFftDataSpace = canvasWidth - diagramThickness * 2;

        var geometry = new PathGeometry();
        diagramFigure = new PathFigure() { IsClosed = true, IsFilled = true };

        diagramPath.Data = geometry;
        geometry.Figures?.Add(diagramFigure);
        diagramFigure.StartPoint = new Point(canvasWidth, canvasHeight - diagramThickness);
        diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(canvasWidth, canvasHeight) });
        diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, canvasHeight) });
        diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, canvasHeight - diagramThickness) });
        for (int i = 0; i < canvasWidth; i++)
        {
            diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(i, canvasHeight - diagramThickness) });
        }
    }

    void DoDiagramUpdate()
    {
        if (audioLibWrapper.PlayState != SoundFlow.Enums.PlaybackState.Playing)
            return;
        float[] fftData = audioLibWrapper.GetCurrentFftSpectrumData();
        var canvasWidth = diagramCanvas!.Bounds.Width;
        var canvasHeight = diagramCanvas.Bounds.Height;
        diagramFftDataSpace = canvasWidth;
        while (diagramFigure!.Segments?.Count < diagramFftDataSpace + diagramNumBorderSegments)
        {
            diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(diagramFigure.Segments.Count, canvasHeight - diagramThickness) });
        }
        while (diagramFigure.Segments?.Count - 1 > diagramFftDataSpace + diagramNumBorderSegments)
        {
            diagramFigure.Segments!.RemoveAt(diagramFigure.Segments.Count - 1);
        }
        for (int i = 0; i < canvasWidth; i++)
        {
            var sampledListVal = fftData[(int)(i / diagramFftDataSpace * fftData.Length / 4)] / 200 * canvasHeight;
            (diagramFigure.Segments![i + diagramNumBorderSegments] as LineSegment)!.Point = new Point(i, canvasHeight - diagramThickness - sampledListVal);
        }
    }

    void UpdateDiagramScaling()
    {
        diagramCanvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "DiagramCanvas")!;
        diagramPath = diagramCanvas.Children.OfType<Path>().FirstOrDefault(x => x.Name == "MyPath")!;
        var geometry = (diagramPath.Data as PathGeometry)!;
        var figure = geometry?.Figures?[0];
        if (figure == null) return;

        var canvasWidth = diagramCanvas.Bounds.Width;
        var canvasHeight = diagramCanvas.Bounds.Height;
        figure.StartPoint = new Point(canvasWidth, canvasHeight - diagramThickness);
        figure.Segments![0] = new LineSegment() { Point = new Point(canvasWidth, canvasHeight) };
        figure.Segments![1] = new LineSegment() { Point = new Point(0, canvasHeight) };
        figure.Segments![2] = new LineSegment() { Point = new Point(0, canvasHeight - diagramThickness) };
    }
}