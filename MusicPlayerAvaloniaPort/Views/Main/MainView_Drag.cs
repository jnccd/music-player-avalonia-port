using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using MusicPlayerAvaloniaPort.Persistence.Configuration;

namespace MusicPlayerAvaloniaPort.Views.Main;

public partial class MainView : UserControl
{
    Point dragPointerSauce, dragGlobalPointerSauce;
    PixelPoint dragWindowPosSauce;
    bool isMovingWindow = false;
    private void MainView_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        dragPointerSauce = e.GetPosition(Window);
        dragGlobalPointerSauce = e.GetPosition(Window) + Window!.Position.ToPoint(1);
        dragWindowPosSauce = Window.Position;

        isMovingWindow = true;
    }
    private void MainView_PointerMoved(object? sender, PointerEventArgs e)
    {
        //Debug.WriteLine("MainView_PointerMoved!");
        if (e.Properties.IsLeftButtonPressed == false)
            isMovingWindow = false;
        if (!isMovingWindow)
            return;

        var newPos = e.GetPosition(Window) + Window!.Position.ToPoint(1);
        var deltaX = dragGlobalPointerSauce.X - newPos.X;
        var deltaY = dragGlobalPointerSauce.Y - newPos.Y;

        var preliminaryNewPos = new PixelPoint((int)-deltaX, (int)-deltaY) + dragWindowPosSauce;
        if (Window != null)
            WindowPlacement.KeepInScreen(Window, preliminaryNewPos);
    }
    private void MainView_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        isMovingWindow = false;

        Config.Data.WindowPositionX = Window!.Position.X;
        Config.Data.WindowPositionY = Window!.Position.Y;
        if (Window != null && Window.FrameSize != null)
        {
            Config.Data.Width = Window.Width;
            Config.Data.Height = Window.Height;
        }
    }
    double CurrentRenderScaling() => Window?.RenderScaling ?? 1;
}
