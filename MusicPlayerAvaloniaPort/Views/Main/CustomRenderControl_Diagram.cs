using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Threading;

namespace MusicPlayerAvaloniaPort.Views.Main;

public class CustomRenderControl_Diagram : Control
{
    AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    Window? window => TopLevel.GetTopLevel(this) as Window;
    UserControl? view => window?.Content as UserControl;

    PathGeometry? diagramGeometry;
    PathFigure? diagramFigure;
    int diagramThickness = 10;
    int diagramNumBorderSegments = 3;
    double diagramFftDataSpace = 0;
    SolidColorBrush? PrimaryColorBrush;

    public CustomRenderControl_Diagram() : base()
    {
        this.Loaded += (s, e) =>
        {
            PrimaryColorBrush = view!.FindResource("PrimaryColor") as SolidColorBrush;

            var controlWidth = this.Bounds.Width;
            var controlHeight = this.Bounds.Height;
            diagramFftDataSpace = controlWidth - diagramThickness * 2;

            diagramFigure = new PathFigure() { IsClosed = true, IsFilled = true };
            diagramGeometry = new PathGeometry();
            diagramGeometry.Figures?.Add(diagramFigure);

            diagramFigure.StartPoint = new Point(controlWidth, controlHeight - diagramThickness);
            diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(controlWidth, controlHeight) });
            diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, controlHeight) });
            diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(0, controlHeight - diagramThickness) });
            for (int i = 0; i < controlWidth; i++)
            {
                diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(i, controlHeight - diagramThickness) });
            }
        };
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);

        Update();
        Draw(context);
    }

    public void Update()
    {
        if (audioLibWrapper.PlayState != SoundFlow.Enums.PlaybackState.Playing)
            return;
        float[] fftData = audioLibWrapper.GetCurrentFftSpectrumData();
        var controlWidth = this.Bounds.Width;
        var controlHeight = this.Bounds.Height;
        diagramFftDataSpace = controlWidth;
        while (diagramFigure!.Segments?.Count < diagramFftDataSpace + diagramNumBorderSegments)
        {
            diagramFigure.Segments!.Add(new LineSegment() { Point = new Point(diagramFigure.Segments.Count, controlHeight - diagramThickness) });
        }
        while (diagramFigure.Segments?.Count - 1 > diagramFftDataSpace + diagramNumBorderSegments)
        {
            diagramFigure.Segments!.RemoveAt(diagramFigure.Segments.Count - 1);
        }
        for (int i = 0; i < controlWidth; i++)
        {
            var sampledListVal = fftData[(int)(i / diagramFftDataSpace * fftData.Length / 4)] / 200 * controlHeight;
            (diagramFigure.Segments![i + diagramNumBorderSegments] as LineSegment)!.Point = new Point(i, controlHeight - diagramThickness - sampledListVal);
        }
    }

    private void Draw(DrawingContext context)
    {
        context.DrawGeometry(PrimaryColorBrush, null, diagramGeometry!);
    }

    public void UpdateDiagramScaling()
    {
        var controlWidth = this.Bounds.Width;
        var controlHeight = this.Bounds.Height;
        diagramFigure?.StartPoint = new Point(controlWidth, controlHeight - diagramThickness);
        diagramFigure?.Segments![0] = new LineSegment() { Point = new Point(controlWidth, controlHeight) };
        diagramFigure?.Segments![1] = new LineSegment() { Point = new Point(0, controlHeight) };
        diagramFigure?.Segments![2] = new LineSegment() { Point = new Point(0, controlHeight - diagramThickness) };
    }
}