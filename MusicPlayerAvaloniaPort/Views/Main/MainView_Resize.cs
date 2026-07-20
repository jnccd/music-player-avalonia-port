using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using MusicPlayerAvaloniaPort.Helpers;

namespace MusicPlayerAvaloniaPort.Views.Main;

enum DragType
{
    Normal,
    ResizeBottomRight,
    ResizeBottomLeft,
    ResizeTopRight,
    ResizeTopLeft,
}

public partial class MainView : UserControl
{
    Point borderDragPointerSauce, borderDragGlobalPointerSauce, borderDragWindowSizeSauce;
    PixelPoint borderDragWindowPosSauce;
    DragType borderDragType = DragType.Normal;
    bool isChangingSizeOrPos = false;

    private void WindowBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Globals.IsDesktop || !e.Properties.IsLeftButtonPressed)
            return;
        if (!OperatingSystem.IsLinux())
            return;
        //Debug.WriteLine($"WindowBorder_PointerPressed {e.GetPosition(this)} {e.Source} {e.Pointer.Type} {e.Properties.IsLeftButtonPressed}");

        //Debug.WriteLine($"{this.Parent?.GetType()}");
        var window = (Parent as Window)!;

        borderDragPointerSauce = e.GetPosition(window);
        borderDragGlobalPointerSauce = e.GetPosition(window) + window.Position.ToPoint(1);
        borderDragWindowPosSauce = window.Position;
        borderDragWindowSizeSauce = new Point(window.Width, window.Height);

        var borderSize = 12;

        if (borderDragPointerSauce.X >= window.Width - borderSize && borderDragPointerSauce.Y >= window.Height - borderSize)
            borderDragType = DragType.ResizeBottomRight;
        else if (borderDragPointerSauce.X <= borderSize && borderDragPointerSauce.Y >= window.Height - borderSize)
            borderDragType = DragType.ResizeBottomLeft;
        else if (borderDragPointerSauce.X >= window.Width - borderSize && borderDragPointerSauce.Y <= borderSize)
            borderDragType = DragType.ResizeTopRight;
        else if (borderDragPointerSauce.X <= borderSize && borderDragPointerSauce.Y <= borderSize)
            borderDragType = DragType.ResizeTopLeft;
        else
            borderDragType = DragType.Normal;

        isChangingSizeOrPos = borderDragPointerSauce.X <= borderSize || borderDragPointerSauce.Y <= borderSize || borderDragPointerSauce.X >= window.Width - borderSize || borderDragPointerSauce.X >= window.Height - borderSize;
    }

    private void WindowBorder_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        //Debug.WriteLine($"WindowBorder_PointerReleased {e.GetPosition(this)} {e.Source} {e.Pointer.Type} {e.Properties.IsLeftButtonPressed}");

        isChangingSizeOrPos = false;
    }

    private void Border_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!Globals.IsDesktop)
            return;
        if (e.Properties.IsLeftButtonPressed == false)
            isChangingSizeOrPos = false;
        if (!isChangingSizeOrPos)
            return;

        var window = (this.Parent as Window)!;
        var newPos = e.GetPosition(window) + window.Position.ToPoint(1);
        var deltaX = borderDragGlobalPointerSauce.X - newPos.X;
        var deltaY = borderDragGlobalPointerSauce.Y - newPos.Y;
        //Debug.WriteLine($"Border_PointerMoved? {dragWindowPosSauce} / {dragGlobalPointerSauce} / {newPos} / {deltaX} / {deltaY} / {dragType} / {window.RenderScaling}");

        double width = window.Width, height = window.Height;

        if (borderDragType == DragType.ResizeBottomRight)
        {
            width = Math.Max(Globals.MinWindowSize.X, borderDragWindowSizeSauce.X - deltaX);
            height = Math.Max(Globals.MinWindowSize.Y, borderDragWindowSizeSauce.Y - deltaY);
            window.Width = width;
            window.Height = height;
        }
        else if (borderDragType == DragType.ResizeBottomLeft)
        {
            width = Math.Max(Globals.MinWindowSize.X, borderDragWindowSizeSauce.X + deltaX / window.RenderScaling);
            height = Math.Max(Globals.MinWindowSize.Y, borderDragWindowSizeSauce.Y - deltaY);
            window.Width = width;
            window.Height = height;
            window.Position = new PixelPoint((int)(-deltaX), (int)(0)) + borderDragWindowPosSauce;
        }
        else if (borderDragType == DragType.ResizeTopLeft)
        {
            width = Math.Max(Globals.MinWindowSize.X, borderDragWindowSizeSauce.X + deltaX / window.RenderScaling);
            height = Math.Max(Globals.MinWindowSize.Y, borderDragWindowSizeSauce.Y + deltaY / window.RenderScaling);
            window.Width = width;
            window.Height = height;
            window.Position = new PixelPoint((int)(-deltaX), (int)(-deltaY)) + borderDragWindowPosSauce;
        }
        else if (borderDragType == DragType.ResizeTopRight)
        {
            width = Math.Max(Globals.MinWindowSize.X, borderDragWindowSizeSauce.X - deltaX);
            height = Math.Max(Globals.MinWindowSize.Y, borderDragWindowSizeSauce.Y + deltaY / window.RenderScaling);
            window.Width = width;
            window.Height = height;
            window.Position = new PixelPoint((int)(0), (int)(-deltaY)) + borderDragWindowPosSauce;
        }
        else if (borderDragType == DragType.Normal)
            window.Position = new PixelPoint((int)(-deltaX), (int)(-deltaY)) + borderDragWindowPosSauce;

        e.Handled = true;
    }
}