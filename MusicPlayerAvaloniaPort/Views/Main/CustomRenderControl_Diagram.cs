using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Visualization;
using Path = Avalonia.Controls.Shapes.Path;

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
    const int fftDiagramNumBorderSegments = 3;
    int fftDiagramFftDataSpace = 0;
    PathGeometry? samplesDiagramGeometry;
    PathFigure? samplesDiagramFigure;

    PathGeometry? currentGeometry = null;
    SolidColorBrush? currentBrush = null;
    IPen? currentPen = null;

    object lockject = new();

    // ---------------------------------------------------------------------------------------------
    // Background "data model" producer.
    //
    // Building one diagram frame means running the FFT analysis, mapping the spectrum bins onto the
    // control width, smoothing it (and, per frame, opening the DB for the song volume). Doing that
    // inside Render - as the old Update() did with Update().Wait() - blocked the UI thread on the
    // whole chain on every frame. Instead the per-frame data is computed on a background task into a
    // reusable float[] ("the model": one normalized value per column of the control) and published
    // under <see cref="lockject"/>. Render then only copies the newest model into the pre-allocated
    // path segments (which have to live on the UI thread) and draws.
    //
    // Only one model computation runs at a time (single-flight). The buffers are recycled: the model
    // the UI is currently reading is never written to again, because the publish swap and every UI
    // read of the published model happen while holding <see cref="lockject"/>.
    // ---------------------------------------------------------------------------------------------
    // 0/1, accessed with Interlocked (it is written by the background producer and read on the UI thread).
    int modelComputeInFlight;
    float[]? publishedModel;
    float[]? modelRecycleBuffer;
    int publishedModelWidth = -1;
    VisMode publishedModelMode;

    /// <summary>
    /// Throttles the self-perpetuating redraw loop to a lower frame rate while low power mode is active
    /// (see <see cref="LowPowerFrameScheduler"/>).
    /// </summary>
    readonly LowPowerFrameScheduler frameScheduler;

    public CustomRenderControl_Diagram() : base()
    {
        frameScheduler = new LowPowerFrameScheduler(
            () => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background),
            () => audioLibWrapper.PlayState == SoundFlow.Enums.PlaybackState.Playing,
            Dispatcher.UIThread);

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
            if (audioLibWrapper.PlayState == SoundFlow.Enums.PlaybackState.Playing)
                frameScheduler.ScheduleNextFrame();

            RequestModelUpdate();
            CopyPublishedModelToFigure();
            Draw(context);
        });
    }

    /// <summary>
    /// Starts a background computation of the per-column model for the current visualization mode
    /// (single-flight). While playback is running every rendered frame requests a fresh model, so the
    /// model cadence follows the (low-power-aware) render cadence; when paused a model is only
    /// requested after something actually changed (mode or size), so the UI thread stays idle.
    /// </summary>
    void RequestModelUpdate()
    {
        if (Interlocked.CompareExchange(ref modelComputeInFlight, 1, 0) != 0)
            return;

        int width = fftDiagramFftDataSpace;
        if (width <= 0)
        {
            Interlocked.Exchange(ref modelComputeInFlight, 0);
            return;
        }

        VisMode mode = currentVisMode;
        bool playing = audioLibWrapper.PlayState == SoundFlow.Enums.PlaybackState.Playing;
        if (!playing && publishedModel != null && publishedModelMode == mode && publishedModelWidth == width)
        {
            Interlocked.Exchange(ref modelComputeInFlight, 0);
            return;
        }

        var _ = Task.Run(async () => await ProduceModel(width, mode));
    }

    async Task ProduceModel(int width, VisMode mode)
    {
        // Recycle the previously published buffer (safe: nothing reads it once the publish swap under
        // the lock handed it over, see the class comment).
        float[] model = modelRecycleBuffer ?? new float[width];
        if (model.Length != width)
            model = new float[width];

        try
        {
            await Program.WrapInTryAsync(async () =>
            {
                switch (mode)
                {
                    case VisMode.SmoothFFT:
                    {
                        float[] fftData = await diagramDataMapper.GetScaledAndSlicedFftData(width);
                        float[] smoothedData = await diagramDataMapper.SmoothenFftData(fftData, width, 1);
                        CopyToModel(smoothedData, model, width);
                        break;
                    }
                    case VisMode.RawFFT:
                    {
                        float[] fftData = await diagramDataMapper.GetScaledAndSlicedFftData(width);
                        CopyToModel(fftData, model, width);
                        break;
                    }
                    case VisMode.Samples:
                    {
                        ReadOnlyMemory<float> sampleData = await audioLibWrapper.GetCurrentlyPlayingSampleData();
                        var sampleDataSpan = sampleData.Span;
                        if (sampleDataSpan.Length == 0)
                            return;
                        for (int i = 0; i < width; i++)
                        {
                            int sampleFrom = (int)(i / (float)width * (sampleDataSpan.Length - 1));
                            model[i] = sampleDataSpan[sampleFrom];
                        }
                        break;
                    }
                }

                // Publish. Both the swap and every UI read of the published model run under lockject,
                // so the buffer handed back for recycling is never being read anymore.
                lock (lockject)
                {
                    modelRecycleBuffer = publishedModel;
                    publishedModel = model;
                    publishedModelWidth = width;
                    publishedModelMode = mode;
                }
            }, EndProgramOnError: false);
        }
        finally
        {
            Interlocked.Exchange(ref modelComputeInFlight, 0);
        }
    }

    static void CopyToModel(float[] source, float[] model, int width)
    {
        if (source.Length >= width)
            Array.Copy(source, 0, model, 0, width);
        else if (source.Length > 0)
            Array.Copy(source, model, source.Length);
    }

    /// <summary>
    /// Copies the newest published model into the path segments of the current visualization mode.
    /// Runs on the UI thread (the PathFigure segments must only be touched there) but only does the
    /// cheap per-column value copy - the FFT/DB/mapping work happened on the background producer.
    /// The published model is only ever read under <see cref="lockject"/> (the producer publishes
    /// under the same lock), so a recycled buffer can never be overwritten while it is being read.
    /// </summary>
    void CopyPublishedModelToFigure()
    {
        // Mirror the original integer arithmetic so the diagram renders pixel-identical values.
        int controlHeight = (int)this.Bounds.Height;
        int usableHeight = controlHeight - fftDiagramThickness;

        lock (lockject)
        {
            if (publishedModel == null || publishedModelMode != currentVisMode)
                return;

            int width = publishedModelWidth;
            if (width != fftDiagramFftDataSpace || width != publishedModel.Length || width <= 0)
                return;

            float[] model = publishedModel;

            if (currentVisMode == VisMode.SmoothFFT)
            {
                if (smoothFftDiagramFigure == null || smoothFftDiagramGeometry == null)
                    return;
                for (int i = 0; i < width; i++)
                {
                    double y = controlHeight - fftDiagramThickness - model[i] * usableHeight;
                    (smoothFftDiagramFigure.Segments![i + fftDiagramNumBorderSegments] as LineSegment)!.Point = new Point(i, y);
                }

                currentGeometry = smoothFftDiagramGeometry;
                currentBrush = PrimaryColorBrush;
                currentPen = null;
            }
            else if (currentVisMode == VisMode.RawFFT)
            {
                if (rawFftDiagramFigure == null || rawFftDiagramGeometry == null)
                    return;
                for (int i = 0; i < width; i++)
                {
                    double y = controlHeight - fftDiagramThickness - model[i] * usableHeight;
                    (rawFftDiagramFigure.Segments![i + fftDiagramNumBorderSegments] as LineSegment)!.Point = new Point(i, y);
                }

                currentGeometry = rawFftDiagramGeometry;
                currentBrush = PrimaryColorBrush;
                currentPen = null;
            }
            else if (currentVisMode == VisMode.Samples)
            {
                if (samplesDiagramFigure == null || samplesDiagramGeometry == null)
                    return;
                for (int i = 0; i < width; i++)
                {
                    double y = controlHeight / 2 + model[i] * (controlHeight / 4);
                    (samplesDiagramFigure.Segments![i] as LineSegment)!.Point = new Point(i, y);
                }
                samplesDiagramFigure.StartPoint = (samplesDiagramFigure.Segments?.First() as LineSegment)!.Point;

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

        lock (lockject)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var controlWidth = (int)this.Bounds.Width;
                var controlHeight = (int)this.Bounds.Height;

                // smoothFftDiagramFigure
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

                // rawFftDiagramFigure
                rawFftDiagramFigure?.StartPoint = new Point(controlWidth, controlHeight - fftDiagramThickness);
                rawFftDiagramFigure?.Segments![0] = new LineSegment() { Point = new Point(controlWidth, controlHeight) };
                rawFftDiagramFigure?.Segments![1] = new LineSegment() { Point = new Point(0, controlHeight) };
                rawFftDiagramFigure?.Segments![2] = new LineSegment() { Point = new Point(0, controlHeight - fftDiagramThickness) };

                while (rawFftDiagramFigure!.Segments?.Count < fftDiagramFftDataSpace + fftDiagramNumBorderSegments)
                {
                    rawFftDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(rawFftDiagramFigure.Segments.Count, controlHeight - fftDiagramThickness) });
                }
                while (rawFftDiagramFigure.Segments?.Count - 1 > fftDiagramFftDataSpace + fftDiagramNumBorderSegments)
                {
                    rawFftDiagramFigure.Segments!.RemoveAt(rawFftDiagramFigure.Segments.Count - 1);
                }

                // samplesDiagramFigure
                while (samplesDiagramFigure!.Segments?.Count < fftDiagramFftDataSpace)
                {
                    samplesDiagramFigure.Segments!.Add(new LineSegment() { Point = new Point(samplesDiagramFigure.Segments.Count, controlHeight / 2) });
                }
                while (samplesDiagramFigure.Segments?.Count - 1 > fftDiagramFftDataSpace)
                {
                    samplesDiagramFigure.Segments!.RemoveAt(samplesDiagramFigure.Segments.Count - 1);
                }
            });
        }
    }
}
