using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Services.Visualization;
using System;

namespace MusicPlayerAvaloniaPort.Views.Main;

enum VisMode { SmoothFFT, RawFFT, Samples }

public class CustomRenderControl_Diagram : Control
{
    AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    DiagramDataMapperService diagramDataMapper = ServiceContainer.GetService<DiagramDataMapperService>();
    Window? window => TopLevel.GetTopLevel(this) as Window;
    UserControl? view => window?.Content as UserControl;

    VisMode currentVisMode = VisMode.SmoothFFT;
    SolidColorBrush? PrimaryColorBrush;
    Pen? PrimaryColorPen;

    PathGeometry? smoothFftDiagramGeometry;
    PathFigure? smoothFftDiagramFigure;
    PathGeometry? rawFftDiagramGeometry;
    PathFigure? rawFftDiagramFigure;
    const int fftDiagramThickness = 10;
    int fftDiagramNumBorderSegments = 3;
    int fftDiagramFftDataSpace = 0;
    PathGeometry? samplesDiagramGeometry;
    PathFigure? samplesDiagramFigure;

    PathGeometry? currentGeometry = null;
    SolidColorBrush? currentBrush = null;
    IPen? currentPen = null;

    object lockject = new();

    public CustomRenderControl_Diagram() : base()
    {
        this.Loaded += (s, e) =>
        {
            PrimaryColorBrush = view!.FindResource("PrimaryColor") as SolidColorBrush;
            PrimaryColorPen = new Pen(PrimaryColorBrush, 1);

            var controlWidth = this.Bounds.Width;
            var controlHeight = this.Bounds.Height;
            fftDiagramFftDataSpace = (int)controlWidth;

            // Smooth
            smoothFftDiagramFigure = new PathFigure() { IsClosed = true, IsFilled = true };
            smoothFftDiagramGeometry = new PathGeometry();
            smoothFftDiagramGeometry.Figures?.Add(smoothFftDiagramFigure);

            smoothFftDiagramFigure.StartPoint = new Point(controlWidth, controlHeight - fftDiagramThickness);
            smoothFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(controlWidth, controlHeight) });
            smoothFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, controlHeight) });
            smoothFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, controlHeight - fftDiagramThickness) });
            for (int i = 0; i < fftDiagramFftDataSpace; i++)
            {
                smoothFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(i, controlHeight - fftDiagramThickness) });
            }

            // Raw
            rawFftDiagramFigure = new PathFigure() { IsClosed = true, IsFilled = true };
            rawFftDiagramGeometry = new PathGeometry();
            rawFftDiagramGeometry.Figures?.Add(rawFftDiagramFigure);

            rawFftDiagramFigure.StartPoint = new Point(controlWidth, controlHeight - fftDiagramThickness);
            rawFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(controlWidth, controlHeight) });
            rawFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, controlHeight) });
            rawFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, controlHeight - fftDiagramThickness) });
            for (int i = 0; i < fftDiagramFftDataSpace; i++)
            {
                rawFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(i, controlHeight - fftDiagramThickness) });
            }

            // Samples
            samplesDiagramFigure = new PathFigure() { IsClosed = false };
            samplesDiagramGeometry = new PathGeometry();
            samplesDiagramGeometry.Figures?.Add(samplesDiagramFigure);
            samplesDiagramFigure.StartPoint = new Point(0, controlHeight / 2);
            for (int i = 0; i < fftDiagramFftDataSpace; i++)
            {
                samplesDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(i, controlHeight / 2) });
            }

            currentGeometry = smoothFftDiagramGeometry;
        };
    }

    public override void Render(DrawingContext context)
    {
        Program.WrapInTry(() =>
        {
            base.Render(context);
            Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);

            Update();
            Draw(context);
        });
    }

    public void Update()
    {
        if (audioLibWrapper.PlayState != SoundFlow.Enums.PlaybackState.Playing)
            return;

        var controlWidth = (int)this.Bounds.Width;
        var controlHeight = (int)this.Bounds.Height;

        if (currentVisMode == VisMode.SmoothFFT)
        {
            float[] fftData = diagramDataMapper.GetScaledAndSlicedFftData(fftDiagramFftDataSpace);
            float[] smoothedData = diagramDataMapper.SmoothenFftData(fftData, fftDiagramFftDataSpace, 1);

            lock (lockject)
            {
                for (int i = 0; i < fftDiagramFftDataSpace; i++)
                {
                    var sampleFrom = (int)(i / (float)fftDiagramFftDataSpace * smoothedData.Length);
                    var sampledListVal = smoothedData[sampleFrom] * (controlHeight - fftDiagramThickness);
                    (smoothFftDiagramFigure?.Segments?[i + fftDiagramNumBorderSegments] as LineSegment)!.Point = new Point(i, controlHeight - fftDiagramThickness - sampledListVal);
                }

                currentGeometry = smoothFftDiagramGeometry;
                currentBrush = PrimaryColorBrush;
                currentPen = null;
            }
        }
        else if (currentVisMode == VisMode.RawFFT)
        {
            float[] fftData = diagramDataMapper.GetScaledAndSlicedFftData(fftDiagramFftDataSpace);

            lock (lockject)
            {
                for (int i = 0; i < fftDiagramFftDataSpace; i++)
                {
                    var sampleFrom = (int)(i / (float)fftDiagramFftDataSpace * fftData.Length);
                    var sampledListVal = fftData[sampleFrom] * (controlHeight - fftDiagramThickness);
                    (rawFftDiagramFigure?.Segments?[i + fftDiagramNumBorderSegments] as LineSegment)!.Point = new Point(i, controlHeight - fftDiagramThickness - sampledListVal);
                }

                currentGeometry = rawFftDiagramGeometry;
                currentBrush = PrimaryColorBrush;
                currentPen = null;
            }
        }
        else if (currentVisMode == VisMode.Samples)
        {
            ReadOnlySpan<float> sampleData = audioLibWrapper.GetCurrentSampleData();

            lock (lockject)
            {
                for (int i = 0; i < fftDiagramFftDataSpace; i++)
                {
                    var sampleFrom = (int)(i / (float)fftDiagramFftDataSpace * (sampleData.Length - 1));
                    var sampledListVal = sampleData[sampleFrom] * (controlHeight / 4);
                    (samplesDiagramFigure?.Segments?[i] as LineSegment)!.Point = new Point(i, controlHeight / 2 + sampledListVal);
                }
                samplesDiagramFigure!.StartPoint = (samplesDiagramFigure?.Segments?.First() as LineSegment)!.Point;

                currentGeometry = samplesDiagramGeometry;
                currentBrush = null;
                currentPen = PrimaryColorPen;
            }
        }
    }

    private void Draw(DrawingContext context)
    {
        if (currentGeometry == null)
            return;

        lock (lockject)
        {
            context.DrawGeometry(currentBrush, currentPen, currentGeometry!);
        }
    }

    public void CycleVisMode()
    {
        if ((int)currentVisMode == Enum.GetValues(typeof(VisMode)).Length - 1)
            currentVisMode = 0;
        else
            currentVisMode++;
    }

    public void UpdateDiagramScaling()
    {
        if (smoothFftDiagramFigure == null || smoothFftDiagramGeometry == null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            lock (lockject)
            {
                var controlWidth = (int)this.Bounds.Width;
                var controlHeight = (int)this.Bounds.Height;
                smoothFftDiagramFigure?.StartPoint = new Point(controlWidth, controlHeight - fftDiagramThickness);
                smoothFftDiagramFigure?.Segments![0] = new LineSegment() { Point = new Point(controlWidth, controlHeight) };
                smoothFftDiagramFigure?.Segments![1] = new LineSegment() { Point = new Point(0, controlHeight) };
                smoothFftDiagramFigure?.Segments![2] = new LineSegment() { Point = new Point(0, controlHeight - fftDiagramThickness) };

                fftDiagramFftDataSpace = (int)controlWidth;
                while (smoothFftDiagramFigure!.Segments?.Count < fftDiagramFftDataSpace + fftDiagramNumBorderSegments)
                {
                    smoothFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(smoothFftDiagramFigure.Segments.Count, controlHeight - fftDiagramThickness) });
                }
                while (smoothFftDiagramFigure.Segments?.Count - 1 > fftDiagramFftDataSpace + fftDiagramNumBorderSegments)
                {
                    smoothFftDiagramFigure.Segments!.RemoveAt(smoothFftDiagramFigure.Segments.Count - 1);
                }
            }
        });
    }
}