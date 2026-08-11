using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Services.Infrastructure;

namespace MusicPlayerAvaloniaPort.Views.Main;

public class CustomRenderControl_Notification : Control
{
    Window? window => TopLevel.GetTopLevel(this) as Window;
    UserControl? view => window?.Content as UserControl;

    // Timing
    Stopwatch stopwatch = new();
    TimeSpan? notificationShowTime;

    // Text
    double textOpacity = 0;
    string? rawTitleText;
    FormattedText? formattedTitleText;
    IBrush? foreground;

    public CustomRenderControl_Notification()
    {
        this.Loaded += (s, e) =>
        {
            stopwatch.Start();
            notificationShowTime = stopwatch.Elapsed;
        };
    }

    public override void Render(DrawingContext context)
    {
        Program.WrapInTry(() =>
        {
            base.Render(context);
            if (textOpacity > 0)
                Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);

            Update();
            Draw(context);
        });
    }

    private void Update()
    {
        if (notificationShowTime == null || formattedTitleText == null)
            return;

        var currentTime = stopwatch.Elapsed;
        var diff = currentTime - notificationShowTime;

        textOpacity = 1 - diff.Value.TotalSeconds / 3;
        foreground = new SolidColorBrush(Colors.White, textOpacity < 0 ? 0 : textOpacity);
        formattedTitleText.SetForegroundBrush(foreground);
    }

    private void Draw(DrawingContext context)
    {
        if (formattedTitleText == null)
            return;

        context.DrawText(formattedTitleText, new Point(3, 3 - (this.Parent as Control)!.Bounds.Height / 2));
    }

    public void ShowUpvoteNotif()
    {
        textOpacity = 1;
        notificationShowTime = stopwatch.Elapsed;
        rawTitleText = "Last Song got Upvoted!";
        foreground = new SolidColorBrush(Colors.White, textOpacity);
        formattedTitleText = new FormattedText(rawTitleText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface((view!.FindResource("BigNoodleTitling") as FontFamily)!, FontStyle.Normal, FontWeight.Normal), 30, foreground);

        Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);
    }

    public void ShowDownvoteNotif()
    {
        textOpacity = 1;
        notificationShowTime = stopwatch.Elapsed;
        rawTitleText = "Last Song got Downvoted!";
        foreground = new SolidColorBrush(Colors.White, textOpacity);
        formattedTitleText = new FormattedText(rawTitleText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface((view!.FindResource("BigNoodleTitling") as FontFamily)!, FontStyle.Normal, FontWeight.Normal), 30, foreground);

        Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);
    }
}