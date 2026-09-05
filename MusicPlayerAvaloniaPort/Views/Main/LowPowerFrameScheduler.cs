using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using System;

namespace MusicPlayerAvaloniaPort.Views.Main;

/// <summary>
/// Schedules the self-perpetuating redraws of a continuously animated custom control (like the
/// diagram, the scrolling title or the progress bar).
/// <para>
/// Normally every rendered frame of such a control queues the next
/// <see cref="Avalonia.Controls.Control.InvalidateVisual"/> right away, so the control (and with it
/// the whole window) redraws as often as the compositor allows. In low power mode that queueing is
/// suspended and the redraws are instead driven by a timer at a reduced frame rate, which cuts the
/// number of full window redraws - and with them the CPU cost of the per-frame FFT analysis,
/// geometry updates and Skia drawing - while a song plays.
/// </para>
/// </summary>
internal sealed class LowPowerFrameScheduler
{
    /// <summary>Frame rate the continuously drawn custom controls run at while low power mode is active.</summary>
    public static readonly TimeSpan LowPowerModeFrameInterval = TimeSpan.FromMilliseconds(1000d / 20d);

    /// <summary>
    /// Queues the next redraw exactly like the control used to before low power mode existed (the
    /// Dispatcher priority and queueing style stay per-control, so non-low-power behaviour is unchanged).
    /// </summary>
    readonly Action scheduleFrame;
    /// <summary>Whether the animation the redraws belong to is currently running (i.e. playback is active).</summary>
    readonly Func<bool> isAnimationRunning;
    readonly Dispatcher dispatcher;

    DispatcherTimer? timer;

    public LowPowerFrameScheduler(Action scheduleFrame, Func<bool> isAnimationRunning, Dispatcher dispatcher)
    {
        this.scheduleFrame = scheduleFrame;
        this.isAnimationRunning = isAnimationRunning;
        this.dispatcher = dispatcher;
    }

    /// <summary>
    /// Call from the control's Render, in the exact spot where it used to schedule its next animation
    /// frame. With low power mode off it schedules the next frame immediately (historical behaviour).
    /// With it on, no immediate next frame is scheduled - the timer below drives the redraws instead.
    /// </summary>
    public void ScheduleNextFrame()
    {
        if (!Config.Data.LowPowerMode)
        {
            StopTimer();
            scheduleFrame();
            return;
        }

        if (timer == null)
        {
            timer = new DispatcherTimer(LowPowerModeFrameInterval, DispatcherPriority.Background, dispatcher);
            timer.Tick += (s, e) =>
            {
                if (!isAnimationRunning())
                {
                    // Nothing animates right now (e.g. playback paused): stop waking up until the
                    // next Render starts the timer again. Keeps the UI thread idle while paused.
                    timer.Stop();
                    return;
                }

                scheduleFrame();
            };
        }

        if (!timer.IsEnabled)
            timer.Start();
    }

    void StopTimer()
    {
        if (timer != null && timer.IsEnabled)
            timer.Stop();
    }
}
