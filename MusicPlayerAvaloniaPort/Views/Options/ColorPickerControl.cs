using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MusicPlayerAvaloniaPort.Views.Options;

/// <summary>
/// The color picker of the options window's General section: a saturation/brightness square for the
/// current hue (saturation on x, brightness on y) above a hue bar, each with a marker for the selected
/// point. This is the Avalonia port's replacement for the Windows Forms ColorDialog the DxMGP client
/// opens from its options menu.
///
/// Colors are picked without alpha (a transparent accent would be invisible). Every change while dragging
/// is reported through <see cref="ColorChanged"/> so the main window repaints live, while
/// <see cref="ColorChangeFinished"/> is only raised once the interaction ended - the caller persists the
/// config there instead of on every intermediate color.
/// </summary>
public class ColorPickerControl : Control
{
    const double SquareHeight = 110;
    const double HueBarHeight = 16;
    const double Gap = 6;
    const double TotalHeight = SquareHeight + Gap + HueBarHeight;
    const double MarkerRadius = 6;
    const double CornerRadius = 4;

    /// <summary>Width used when the control is measured with an unconstrained width (e.g. in a StackPanel).</summary>
    const double DefaultWidth = 260;

    static readonly LinearGradientBrush SaturationOverlay = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.White, 0),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
        }
    };

    static readonly LinearGradientBrush BrightnessOverlay = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
            new GradientStop(Colors.Black, 1),
        }
    };

    static readonly LinearGradientBrush HueBarBackground = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.Red, 0),
            new GradientStop(Colors.Yellow, 1 / 6.0),
            new GradientStop(Colors.Lime, 2 / 6.0),
            new GradientStop(Colors.Cyan, 3 / 6.0),
            new GradientStop(Colors.Blue, 4 / 6.0),
            new GradientStop(Colors.Magenta, 5 / 6.0),
            new GradientStop(Colors.Red, 1),
        }
    };

    static readonly IPen OutlinePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    static readonly IPen MarkerShadowPen = new Pen(Brushes.Black, 1);
    static readonly IPen MarkerPen = new Pen(Brushes.White, 2);

    /// <summary>Background of the saturation/brightness square; only its color changes with the hue.</summary>
    readonly SolidColorBrush hueBackgroundBrush = new(Colors.Red);

    double hue;
    double saturation = 1;
    double brightness = 1;

    DragTarget dragTarget;

    enum DragTarget
    {
        None,
        SaturationBrightness,
        Hue
    }

    public ColorPickerControl()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Raised for every color the user picks, including the intermediate ones of a drag.</summary>
    public event Action<Color>? ColorChanged;

    /// <summary>Raised when the user let go of the picker, i.e. when the picked color is final.</summary>
    public event Action? ColorChangeFinished;

    public Color Color => HsvColor.FromHsv(hue, saturation, brightness).ToRgb();

    /// <summary>
    /// Moves the picker onto an existing color (the persisted one when the options window opens, or the
    /// default after a reset). <paramref name="notify"/> is off by default so showing a color is not
    /// reported back to the caller as a user pick.
    /// </summary>
    public void SetColor(Color color, bool notify = false)
    {
        var hsv = color.ToHsv();
        hue = hsv.H;
        saturation = hsv.S;
        brightness = hsv.V;
        InvalidateVisual();

        if (notify)
            ColorChanged?.Invoke(Color);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : DefaultWidth;
        return new Size(width, TotalHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        if (width <= 0)
            return;

        // Saturation/brightness square: the pure hue, overlaid with white on the saturation axis and with
        // black on the brightness axis.
        var square = new Rect(0, 0, width, SquareHeight);
        hueBackgroundBrush.Color = HsvColor.FromHsv(hue, 1, 1).ToRgb();
        context.DrawRectangle(hueBackgroundBrush, null, square, CornerRadius, CornerRadius);
        context.DrawRectangle(SaturationOverlay, null, square, CornerRadius, CornerRadius);
        context.DrawRectangle(BrightnessOverlay, null, square, CornerRadius, CornerRadius);
        context.DrawRectangle(null, OutlinePen, square, CornerRadius, CornerRadius);

        var hueBar = new Rect(0, SquareHeight + Gap, width, HueBarHeight);
        context.DrawRectangle(HueBarBackground, null, hueBar, CornerRadius, CornerRadius);
        context.DrawRectangle(null, OutlinePen, hueBar, CornerRadius, CornerRadius);

        // Markers, kept inside the rounded corners so they stay fully visible at the extremes.
        var squareMarker = new Point(
            Math.Clamp(saturation * width, MarkerRadius, width - MarkerRadius),
            Math.Clamp((1 - brightness) * SquareHeight, MarkerRadius, SquareHeight - MarkerRadius));
        context.DrawEllipse(null, MarkerShadowPen, squareMarker, MarkerRadius + 1, MarkerRadius + 1);
        context.DrawEllipse(null, MarkerPen, squareMarker, MarkerRadius, MarkerRadius);

        double hueMarkerX = Math.Clamp(hue / 360 * width, 2, width - 2);
        var hueMarker = new Rect(hueMarkerX - 2, hueBar.Y, 4, hueBar.Height);
        context.DrawRectangle(null, MarkerShadowPen, hueMarker, 2, 2);
        context.DrawRectangle(null, MarkerPen, hueMarker, 2, 2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var position = e.GetPosition(this);
        if (position.Y <= SquareHeight)
            dragTarget = DragTarget.SaturationBrightness;
        else if (position.Y >= SquareHeight + Gap)
            dragTarget = DragTarget.Hue;
        else
            return; // The gap between the two areas is not draggable.

        e.Pointer.Capture(this);
        UpdateFromPointer(position);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (dragTarget == DragTarget.None)
            return;

        UpdateFromPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        var finishedTarget = dragTarget;
        dragTarget = DragTarget.None;
        e.Pointer.Capture(null);

        // A release that never started a drag (e.g. in the gap between the two areas) has nothing to finish.
        if (finishedTarget != DragTarget.None)
            ColorChangeFinished?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        // The pointer can be lost without a release (e.g. the window closed mid-drag): still let the caller
        // persist whatever was picked.
        if (dragTarget == DragTarget.None)
            return;

        dragTarget = DragTarget.None;
        ColorChangeFinished?.Invoke();
    }

    void UpdateFromPointer(Point position)
    {
        double width = Math.Max(1, Bounds.Width);

        if (dragTarget == DragTarget.SaturationBrightness)
        {
            saturation = Math.Clamp(position.X / width, 0, 1);
            brightness = 1 - Math.Clamp(position.Y / SquareHeight, 0, 1);
        }
        else if (dragTarget == DragTarget.Hue)
        {
            hue = Math.Clamp(position.X / width, 0, 1) * 360;
        }
        else
        {
            return;
        }

        InvalidateVisual();
        ColorChanged?.Invoke(Color);
    }
}
