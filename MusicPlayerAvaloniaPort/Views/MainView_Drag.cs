using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using MusicPlayerAvaloniaPort.Configuration;

namespace MusicPlayerAvaloniaPort;

public partial class MainView : UserControl
{
    Point dragPointerSauce, dragGlobalPointerSauce;
    PixelPoint dragWindowPosSauce;
    bool isMovingWindow = false;
    private void MainView_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        dragPointerSauce = e.GetPosition(window);
        dragGlobalPointerSauce = e.GetPosition(window) + window!.Position.ToPoint(1);
        dragWindowPosSauce = window.Position;

        isMovingWindow = true;
    }
    private void MainView_PointerMoved(object? sender, PointerEventArgs e)
    {
        Debug.WriteLine("MainView_PointerMoved!");
        if (e.Properties.IsLeftButtonPressed == false)
            isMovingWindow = false;
        if (!isMovingWindow)
            return;

        var newPos = e.GetPosition(window) + window!.Position.ToPoint(1);
        var deltaX = dragGlobalPointerSauce.X - newPos.X;
        var deltaY = dragGlobalPointerSauce.Y - newPos.Y;

        var preliminaryNewPos = new PixelPoint((int)-deltaX, (int)-deltaY) + dragWindowPosSauce;
        var keepInScreenCoords = KeepWindowInScreen(new PixelRect(preliminaryNewPos, PixelSize.FromSize(new Size(window.Bounds.Width, window.Bounds.Height), CurrentRenderScaling())));
        window.Position = keepInScreenCoords;
    }
    private void MainView_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        isMovingWindow = false;

        Config.Data.Pos = window!.Position;
        if (window != null && window.FrameSize != null)
        {
            Config.Data.Width = window.FrameSize.Value.Width;
            Config.Data.Height = window.FrameSize.Value.Height;
        }
        Config.Save();
    }
    double CurrentRenderScaling() => this.VisualRoot?.RenderScaling ?? 1;
    PixelPoint[] WindowPoints = new PixelPoint[4];
    PixelPoint Diff;
    static int X, Y;
    public PixelPoint KeepWindowInScreen(PixelRect WindowBounds)
    {
        PixelRect[] ScreenBoxes = new PixelRect[window!.Screens.ScreenCount];

        for (int i = 0; i < ScreenBoxes.Length; i++)
            ScreenBoxes[i] = new PixelRect(window!.Screens.All[i].WorkingArea.X, window!.Screens.All[i].WorkingArea.Y,
                window!.Screens.All[i].WorkingArea.Width, window!.Screens.All[i].WorkingArea.Height - 56);

        WindowPoints[0] = new PixelPoint(WindowBounds.X, WindowBounds.Y);
        WindowPoints[1] = new PixelPoint(WindowBounds.X + WindowBounds.Width, WindowBounds.Y);
        WindowPoints[2] = new PixelPoint(WindowBounds.X, WindowBounds.Y + WindowBounds.Height);
        WindowPoints[3] = new PixelPoint(WindowBounds.X + WindowBounds.Width, WindowBounds.Y + WindowBounds.Height);

        var scaling = CurrentRenderScaling();
        Screen Main = window!.Screens.All.FirstOrDefault(x => x.Bounds.Contains(window.Position + PixelPoint.FromPoint(new Point(window.Width / 2, window.Height / 2), scaling))) ?? window!.Screens.Primary!;

        for (int i = 0; i < WindowPoints.Length; i++)
            if (!ScreenBoxes.Any(x => x.Contains(WindowPoints[i])))
            {
                Diff = PointRectDiff(WindowPoints[i], new PixelRect(Main.WorkingArea.X, Main.WorkingArea.Y, Main.WorkingArea.Width, Main.WorkingArea.Height));

                if (Diff != new PixelPoint(0, 0))
                {
                    WindowBounds = new PixelRect(WindowBounds.X + Diff.X, WindowBounds.Y + Diff.Y, WindowBounds.Width, WindowBounds.Height);

                    WindowPoints[0] = new PixelPoint(WindowBounds.X, WindowBounds.Y);
                    WindowPoints[1] = new PixelPoint(WindowBounds.X + WindowBounds.Width, WindowBounds.Y);
                    WindowPoints[2] = new PixelPoint(WindowBounds.X, WindowBounds.Y + WindowBounds.Height);
                    WindowPoints[3] = new PixelPoint(WindowBounds.X + WindowBounds.Width, WindowBounds.Y + WindowBounds.Height);
                }
            }

        return new PixelPoint(WindowBounds.X, WindowBounds.Y);
    }
    static PixelPoint PointRectDiff(PixelPoint P, PixelRect R)
    {
        if (P.X > R.X && P.X < R.X + R.Width &&
            P.Y > R.Y && P.Y < R.Y + R.Height)
            return new PixelPoint(0, 0);
        else
        {
            X = 0; Y = 0;
            if (P.X < R.X)
                X = R.X - P.X;
            if (P.X > R.X + R.Width)
                X = R.X + R.Width - P.X;
            if (P.Y < R.Y)
                Y = R.Y - P.Y;
            if (P.Y > R.Y + R.Height)
                Y = R.Y + R.Height - P.Y;
            return new PixelPoint(X, Y);
        }
    }
}