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

public class CustomRenderControl_Title : Control
{
    readonly AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    Window? window => TopLevel.GetTopLevel(this) as Window;
    UserControl? view => window?.Content as UserControl;

    // Timing
    Stopwatch stopwatch = new();
    uint frameCounter = 0;
    TimeSpan? titleLastUpdateTime = null;
    double titleLastPlainTitleTime = -1;
    double titleInverseOpacityMaskAnimSpeed = 0.6;

    // Positioning
    double titleGap = 60;
    double? titleText1X;
    double? titleText2X;
    double? titleTextWidth;

    // Text
    string? rawTitleText;
    FormattedText? formattedTitleText;

    // Fade out/in
    LinearGradientBrush? titleInitialOpacityMask = null;
    double opacityMaskStartX => (titleInitialOpacityMask?.GradientStops.Skip(1).FirstOrDefault()?.Offset ?? 0.2) / 2;

    public CustomRenderControl_Title()
    {
        stopwatch.Start();
        titleLastUpdateTime = stopwatch.Elapsed;

        titleInitialOpacityMask = this.OpacityMask as LinearGradientBrush;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);

        if (rawTitleText == null || formattedTitleText == null)
            return;

        Update();
        Draw(context);
    }

    private void Update()
    {
        var currentTime = stopwatch.Elapsed;
        if (audioLibWrapper.PlayState != SoundFlow.Enums.PlaybackState.Playing)
        {
            titleLastUpdateTime = stopwatch.Elapsed;
            return;
        }
        var movement = (currentTime! - titleLastUpdateTime!).Value.Milliseconds / 69.0;

        if (this.Bounds.Width > titleTextWidth)
        {
            Dispatcher.UIThread.InvokeAsync(() => this.OpacityMask = null, DispatcherPriority.Background);

            titleText1X = 0;
            titleText2X = -9999;

            titleLastPlainTitleTime = frameCounter;
        }
        else
        {
            titleText1X -= movement;
            titleText2X = titleText1X + titleTextWidth + titleGap;
            if (titleText1X + titleTextWidth < 0)
            {
                titleText1X = titleText2X;
                titleText2X = titleText1X + titleTextWidth + titleGap;
            }

            Dispatcher.UIThread.InvokeAsync(() => this.OpacityMask = titleInitialOpacityMask, DispatcherPriority.Background);
            var timeSinceLastPlainTitle = frameCounter - titleLastPlainTitleTime;
            if (timeSinceLastPlainTitle < 300)
            {
                titleInitialOpacityMask?.StartPoint = new RelativePoint(titleInverseOpacityMaskAnimSpeed / -timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
                titleInitialOpacityMask?.EndPoint = new RelativePoint(1 + titleInverseOpacityMaskAnimSpeed / timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
            }
        }

        titleLastUpdateTime = stopwatch.Elapsed;
    }

    private void Draw(DrawingContext context)
    {
        context.DrawText(formattedTitleText!, new Point(titleText1X ?? 0, 0));
        context.DrawText(formattedTitleText!, new Point(titleText2X ?? 0, 0));
    }

    public void UpdateTitleText(string newTitle)
    {
        rawTitleText = newTitle;
        formattedTitleText = new FormattedText(rawTitleText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface((view!.FindResource("BigNoodleTitling") as FontFamily)!, FontStyle.Normal, FontWeight.Normal), 35, new SolidColorBrush(Colors.White));

        titleTextWidth = formattedTitleText.Width;

        titleText1X = titleTextWidth * opacityMaskStartX;
        titleText2X = titleText1X + titleTextWidth + titleGap;
    }
}