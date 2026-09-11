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
    /// <summary>
    /// Whether the fade mask is applied to the current frame. This used to be an assignment of
    /// <see cref="Control.OpacityMask"/>, but that mask is resolved against the bounds of the drawn
    /// content instead of the control's own rectangle, so the fade is now pushed explicitly around the
    /// text (see <see cref="Draw"/>).
    /// </summary>
    bool fadeEnabled;
    double opacityMaskStartX => (titleInitialOpacityMask?.GradientStops.Skip(1).FirstOrDefault()?.Offset ?? 0.2) / 2;

    /// <summary>
    /// Throttles the self-perpetuating redraw loop to a lower frame rate while low power mode is active
    /// (see <see cref="LowPowerFrameScheduler"/>). The title scrolls by elapsed time per frame, so it
    /// keeps its speed at any frame rate.
    /// </summary>
    readonly LowPowerFrameScheduler frameScheduler;

    public CustomRenderControl_Title()
    {
        frameScheduler = new LowPowerFrameScheduler(
            () => Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background),
            () => audioLibWrapper.PlayState == SoundFlow.Enums.PlaybackState.Playing,
            Dispatcher.UIThread);

        this.Loaded += (s, e) =>
        {
            stopwatch.Start();
            titleLastUpdateTime = stopwatch.Elapsed;

            titleInitialOpacityMask = this.OpacityMask as LinearGradientBrush;

            this.OpacityMask = null;
            UpdateTitleText("Loading...", initial1X: 0, initial2X: -9999);
        };
    }

    public override void Render(DrawingContext context)
    {
        Program.WrapInTry(() =>
        {
            base.Render(context);
            if (audioLibWrapper.PlayState == SoundFlow.Enums.PlaybackState.Playing)
                frameScheduler.ScheduleNextFrame();

            if (rawTitleText == null || formattedTitleText == null)
                return;

            Update();
            Draw(context);
        });
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
            fadeEnabled = false;

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

            fadeEnabled = true;
            var timeSinceLastPlainTitle = frameCounter - titleLastPlainTitleTime;
            if (timeSinceLastPlainTitle < 300)
            {
                titleInitialOpacityMask?.StartPoint = new RelativePoint(titleInverseOpacityMaskAnimSpeed / -timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
                titleInitialOpacityMask?.EndPoint = new RelativePoint(1 + titleInverseOpacityMaskAnimSpeed / timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
            }
        }

        titleLastUpdateTime = stopwatch.Elapsed;
        frameCounter++;
    }

    private void Draw(DrawingContext context)
    {
        double title1X = titleText1X ?? 0;
        double title2X = titleText2X ?? 0;

        // The fade has to be anchored to this control's own rectangle. Assigning Control.OpacityMask let
        // the gradient resolve against the bounds of the drawn content instead, so as soon as the
        // leftmost glyph was away from the control's left edge (initial gap, or scrolled far left) the
        // whole fade travelled with the text and started at the glyph rather than at the border. Pushing
        // the mask with an explicit rectangle pins its 0% / 100% to the control's edges, which is what
        // the XAML-declared mask (0% left, 100% right) is meant to describe.
        if (fadeEnabled && titleInitialOpacityMask != null)
        {
            using (context.PushOpacityMask(titleInitialOpacityMask, new Rect(this.Bounds.Size)))
            {
                context.DrawText(formattedTitleText!, new Point(title1X, 0));
                context.DrawText(formattedTitleText!, new Point(title2X, 0));
            }
        }
        else
        {
            context.DrawText(formattedTitleText!, new Point(title1X, 0));
            context.DrawText(formattedTitleText!, new Point(title2X, 0));
        }
    }

    public void UpdateTitleText(string newTitle, int? initial1X = null, int? initial2X = null)
    {
        rawTitleText = newTitle;
        formattedTitleText = new FormattedText(rawTitleText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface((view!.FindResource("BigNoodleTitling") as FontFamily)!, FontStyle.Normal, FontWeight.Normal), 35, new SolidColorBrush(Colors.White));

        titleTextWidth = formattedTitleText.Width;

        titleText1X = initial1X ?? titleTextWidth * opacityMaskStartX;
        titleText2X = initial2X ?? titleText1X + titleTextWidth + titleGap;
    }
}