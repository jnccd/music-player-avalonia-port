using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using Path = Avalonia.Controls.Shapes.Path;

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
    /// <summary>
    /// Reused across all notification frames - the old code allocated a new SolidColorBrush (and
    /// re-set it on the formatted text) on every rendered frame of the fade-out.
    /// </summary>
    readonly SolidColorBrush whiteBrush = new(Colors.White);

    /// <summary>
    /// The upvote notification leads with the same icon the upvote button uses. The geometry is read
    /// from that button (and cached) instead of duplicating its path data here; if the button is not in
    /// the tree the notification simply falls back to plain text.
    /// </summary>
    Geometry? upvoteIcon;
    bool upvoteIconResolved;
    bool showUpvoteIcon;

    /// <summary>Size the upvote icon is authored in (see the upvote button path in MainView.axaml).</summary>
    const double UpvoteIconSourceSize = 24;
    const double NotificationIconSize = 30;
    const double NotificationIconTextGap = 8;

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
        whiteBrush.Opacity = textOpacity < 0 ? 0 : textOpacity > 1 ? 1 : textOpacity;
        foreground = whiteBrush;
        formattedTitleText.SetForegroundBrush(foreground);
    }

    private void Draw(DrawingContext context)
    {
        if (formattedTitleText == null)
            return;

        double top = 0 - (this.Parent as Control)!.Bounds.Height / 2;
        double textX = 3;

        // Draw the upvote icon first and push the text over by its width. Icon and text share the same
        // foreground brush, so they fade out together.
        if (showUpvoteIcon && TryGetUpvoteIcon() is { } icon)
        {
            double iconY = top + (formattedTitleText.Height - NotificationIconSize) / 2;
            double iconScale = NotificationIconSize / UpvoteIconSourceSize;
            using (context.PushTransform(
                Matrix.CreateScale(new Vector(iconScale, iconScale)) *
                Matrix.CreateTranslation(textX, iconY)))
            {
                context.DrawGeometry(foreground, null, icon);
            }

            textX += NotificationIconSize + NotificationIconTextGap;
        }

        context.DrawText(formattedTitleText, new Point(textX, top));
    }

    /// <summary>
    /// Resolves (once) the upvote icon geometry from the upvote button in the view.
    /// </summary>
    Geometry? TryGetUpvoteIcon()
    {
        if (upvoteIconResolved)
            return upvoteIcon;

        upvoteIconResolved = true;
        var upvoteButton = view?
            .GetLogicalDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Name == "ButtonUpvote");
        upvoteIcon = upvoteButton?
            .GetLogicalChildren()
            .OfType<Path>()
            .FirstOrDefault()?
            .Data;

        return upvoteIcon;
    }

    public void ShowUpvoteNotif()
    {
        textOpacity = 1;
        notificationShowTime = stopwatch.Elapsed;
        showUpvoteIcon = true;
        rawTitleText = "Last Song got Upvoted!";
        whiteBrush.Opacity = 1;
        foreground = whiteBrush;
        formattedTitleText = new FormattedText(rawTitleText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface((view!.FindResource("BigNoodleTitling") as FontFamily)!, FontStyle.Normal, FontWeight.Normal), 30, foreground);

        Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);
    }

    public void ShowDownvoteNotif()
    {
        textOpacity = 1;
        notificationShowTime = stopwatch.Elapsed;
        showUpvoteIcon = false;
        rawTitleText = "Last Song got Downvoted!";
        whiteBrush.Opacity = 1;
        foreground = whiteBrush;
        formattedTitleText = new FormattedText(rawTitleText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface((view!.FindResource("BigNoodleTitling") as FontFamily)!, FontStyle.Normal, FontWeight.Normal), 30, foreground);

        Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);
    }
}