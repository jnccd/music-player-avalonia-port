using System;
using Avalonia;
using Avalonia.Controls;

namespace MusicPlayerAvaloniaPort.Views.Main;

/// <summary>
/// Keeps the main window inside the working area of the screen it is on.
///
/// The subtlety that makes this more than a rectangle clamp: <see cref="Window.Position"/> is the origin of the
/// window FRAME as the platform defines it, not the origin of the content the user sees. The two differ by a
/// per-platform, per-scaling amount - measured (13, 1) physical pixels at 200% and (7, 1) at 100% on Windows,
/// and the window manager's border thickness on X11 - and on Wayland <see cref="Window.Position"/> cannot be set
/// at all (the compositor owns window placement, see <see cref="VisibleOriginOffset"/>). Clamping the window
/// position to the working area therefore leaves exactly that offset as a visible gap along the top and left
/// edge, which is what the clamp did before. So the visible rectangle is the one that gets clamped, and the
/// frame offset is translated back out of the result.
///
/// Everything here is in physical pixels: the working area comes from Avalonia already in physical pixels, and
/// window and screen geometry in device independent pixels is converted with the window's render scaling.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Clamps <paramref name="desiredFramePosition"/> so the window's visible area stays inside the working area
    /// of the screen it is being dragged on, and applies it (the window has already been moved when this
    /// returns). This is what the drag handler calls.
    ///
    /// One pass is enough: the visible rectangle (the frame position plus the measured frame offset) is what
    /// gets clamped, which is the only thing that has to touch the screen edge - clamping the frame position
    /// instead is what used to leave the visible gap along the top and left edge, because the frame offset is
    /// part of neither the position nor the window size.
    /// </summary>
    public static PixelPoint KeepInScreen(Window window, PixelPoint desiredFramePosition)
    {
        var desiredBounds = FrameBounds(window, desiredFramePosition);
        var workArea = FindWorkArea(window, desiredBounds);
        var visibleOriginOffset = VisibleOriginOffset(window);

        var fittedBounds = FitIntoWorkArea(desiredBounds, workArea, visibleOriginOffset);
        var position = new PixelPoint(fittedBounds.X, fittedBounds.Y);

        if (position != window.Position)
            window.Position = position;

        return position;
    }

    /// <summary>
    /// The working area of the screen a rectangle belongs to: the one it overlaps most, or the nearest one when
    /// it overlaps none of them. The window rectangle is judged as a whole - a window that was dragged across a
    /// screen border ends up on whichever of the two screens now holds most of it, which is what a user expects
    /// from a drag. This also replaces the old lookup by the screen under a "window center" point that mixed
    /// logical and physical pixels and could therefore pick the wrong monitor entirely on a scaled screen.
    /// </summary>
    public static Rect FindWorkArea(Window window, PixelRect windowBounds)
    {
        var screens = window.Screens;
        Rect best = ToRect(screens.Primary?.WorkingArea ?? screens.All[0].WorkingArea);
        long bestOverlap = -1;
        long bestDistance = long.MaxValue;

        for (int i = 0; i < screens.ScreenCount; i++)
        {
            var workArea = ToRect(screens.All[i].WorkingArea);
            long overlap = OverlapArea(windowBounds, workArea);
            long distance = DistanceToArea(windowBounds, workArea);

            if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
            {
                bestOverlap = overlap;
                bestDistance = distance;
                best = workArea;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the frame position at which the window's visible rectangle sits inside <paramref name="workArea"/>:
    /// flush against its top/left edge when the window fits (or when it is too large, which then overflows to the
    /// right/bottom), flush against its bottom/right edge otherwise, and never moved when it is already inside.
    /// </summary>
    /// <param name="desiredFrameBounds">Where the drag wants to put the window frame.</param>
    /// <param name="visibleOriginOffset">
    /// Offset from the frame origin to the visible (client) origin in physical pixels - see
    /// <see cref="VisibleOriginOffset"/>.
    /// </param>
    public static PixelRect FitIntoWorkArea(PixelRect desiredFrameBounds, Rect workArea, PixelPoint visibleOriginOffset)
    {
        double visibleLeft = desiredFrameBounds.X + visibleOriginOffset.X;
        double visibleTop = desiredFrameBounds.Y + visibleOriginOffset.Y;
        double visibleWidth = desiredFrameBounds.Width;
        double visibleHeight = desiredFrameBounds.Height;

        double left = ClampAxis(visibleLeft, visibleWidth, workArea.X, workArea.Width);
        double top = ClampAxis(visibleTop, visibleHeight, workArea.Y, workArea.Height);

        return new PixelRect(
            (int)Math.Round(left - visibleOriginOffset.X),
            (int)Math.Round(top - visibleOriginOffset.Y),
            desiredFrameBounds.Width,
            desiredFrameBounds.Height);
    }

    static double ClampAxis(double visibleStart, double visibleSize, double areaStart, double areaSize)
    {
        double latestVisibleStart = areaStart + areaSize - visibleSize;
        if (latestVisibleStart < areaStart)
        {
            // The window is larger than the working area. Pin it to the top/left edge so its beginning (the
            // header) stays visible and reachable instead of centering it and cutting off the controls there.
            return areaStart;
        }

        return Math.Clamp(visibleStart, areaStart, latestVisibleStart);
    }

    /// <summary>
    /// Offset from the window frame origin (<see cref="Window.Position"/>) to the origin of the visible client
    /// area, in physical pixels - the correction the clamp has to apply so the visible content, not the frame
    /// border, ends up flush with the working area.
    ///
    /// Measured, not derived, so it follows whatever convention the current backend uses:
    /// <list type="bullet">
    /// <item>Win32: <see cref="Window.Position"/> is the outer frame, the client origin sits the DWM resize
    /// border inside it - measured (13, 1) physical pixels at 200% scaling on this machine.</item>
    /// <item>X11: the backend already converts its internal client position by the <c>_NET_FRAME_EXTENTS</c>
    /// border, and its PointToScreen returns that same client origin, so the same subtraction yields the window
    /// manager's border thickness (zero for a borderless frame).</item>
    /// <item>Wayland: <see cref="Window.Position"/> is always (0, 0) and <c>Move</c> is a documented no-op
    /// ("Not supported by Wayland"), <see cref="Window.FrameSize"/> is null, so this returns (0, 0) and the
    /// clamp degenerates to a rectangle clamp whose result the compositor ignores - the platform does not let a
    /// client place its own windows, and nothing here can change that.</item>
    /// </list>
    /// Deriving it from <see cref="Window.FrameSize"/> instead is NOT equivalent on Win32: the frame border is
    /// not split evenly (the frame is 26x14 physical pixels larger than the client area at 200%, but the client
    /// origin only sits 13,1 pixels inside it).
    /// </summary>
    public static PixelPoint VisibleOriginOffset(Window window)
    {
        if (window.FrameSize is null)
            return default; // No platform window yet: the visible origin is the frame origin.

        try
        {
            var clientOrigin = window.PointToScreen(new Point(0, 0));
            return new PixelPoint(
                (int)Math.Round((double)clientOrigin.X) - window.Position.X,
                (int)Math.Round((double)clientOrigin.Y) - window.Position.Y);
        }
        catch (InvalidOperationException)
        {
            // The platform implementation is not there yet (the window was not shown): the clamp then
            // degenerates to a plain rectangle clamp until it is.
            return default;
        }
    }

    /// <summary>
    /// The window's frame rectangle: <see cref="Window.Position"/> (the frame origin) with the size of the
    /// visible area, so it is the rectangle the caller is trying to move. Read from the visible size - the
    /// physical size of that area - instead of from the logical <see cref="Window.Width"/>/<see cref="Window.Height"/>,
    /// which are not always in sync with it (a window restored from configuration, or one the window manager
    /// resized, would otherwise be clamped with stale dimensions).
    /// </summary>
    public static PixelRect FrameBounds(Window window, PixelPoint position)
    {
        var visibleSize = VisiblePixelSize(window);
        return new PixelRect(position.X, position.Y, visibleSize.Width, visibleSize.Height);
    }

    /// <summary>Physical pixel size of the window's visible area.</summary>
    public static PixelSize VisiblePixelSize(Window window)
    {
        double scaling = window.RenderScaling;
        Size logicalSize = window.ClientSize;

        if (logicalSize.Width <= 0 || logicalSize.Height <= 0)
            logicalSize = window.Bounds.Size;

        return new PixelSize(
            Math.Max(1, (int)Math.Round((double)logicalSize.Width * scaling)),
            Math.Max(1, (int)Math.Round((double)logicalSize.Height * scaling)));
    }

    static Rect ToRect(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    static long OverlapArea(PixelRect a, Rect b)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
            return 0;

        return (long)Math.Round((double)(right - left)) * (long)Math.Round((double)(bottom - top));
    }

    /// <summary>
    /// Squared gap between a rectangle and an area (zero when they overlap). Squared so the value stays
    /// comparable as a <see cref="long"/> even for a window that is far outside every screen.
    /// </summary>
    static long DistanceToArea(PixelRect rect, Rect area)
    {
        double gapX = Math.Max(0, Math.Max(area.X - (rect.X + rect.Width), rect.X - (area.X + area.Width)));
        double gapY = Math.Max(0, Math.Max(area.Y - (rect.Y + rect.Height), rect.Y - (area.Y + area.Height)));
        return (long)Math.Round(gapX) * (long)Math.Round(gapX) + (long)Math.Round(gapY) * (long)Math.Round(gapY);
    }
}
