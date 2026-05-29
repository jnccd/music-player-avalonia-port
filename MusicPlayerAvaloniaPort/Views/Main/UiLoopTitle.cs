using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Services;
using MusicPlayerAvaloniaPort.Services.UiUpdateLoop;
using Path = Avalonia.Controls.Shapes.Path;

namespace MusicPlayerAvaloniaPort.Views.Main;

[RegisterUiLoop]
public class UiLoopTitle() : IUiUpdateLoop(typeof(MainView), typeof(Input))
{
    public record Input(
        Canvas titleCanvas
        ) : IUiUpdateLoopInput;
    readonly AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();

    Stopwatch stopwatch = new();
    double titleLastPlainTitleTime = -1;
    double titleInverseOpacityMaskAnimSpeed = 0.6;
    double titleGap = 60;
    LinearGradientBrush? titleInitialOpacityMask = null;
    TextBlock? titleCanvasText1 = null;
    TextBlock? titleCanvasText2 = null;
    TimeSpan? titleLastUpdateTime = null;
    double? titleCanvasText1X;
    double? titleCanvasText2X;
    double? titleCanvasText1Width;

    public override void Init(IUiUpdateLoopInput uiUpdateLoopInput)
    {
        var input = uiUpdateLoopInput as Input ?? throw new InvalidDataException("Invalid input for UiLoopDiagram");

        stopwatch.Start();
        titleLastUpdateTime = stopwatch.Elapsed;

        //titleCanvas = this.GetLogicalDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "TitleCanvas")!;
        titleInitialOpacityMask = input.titleCanvas.OpacityMask as LinearGradientBrush;
        titleCanvasText1 = input.titleCanvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText1")!;
        titleCanvasText2 = input.titleCanvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText2")!;

        // Needs to be invalidated when the title changes
        titleCanvasText1.Measure(Size.Infinity);
        titleCanvasText1Width = titleCanvasText1.DesiredSize.Width;

        titleCanvasText1X = input.titleCanvas.Bounds.Width * ((titleInitialOpacityMask?.GradientStops.Skip(1).FirstOrDefault()?.Offset ?? 0.2) / 2);
        titleCanvasText2X = titleCanvasText1X + titleCanvasText1Width + titleGap;
    }

    public override void Update(IUiUpdateLoopInput uiUpdateLoopInput, ulong frameCounter)
    {
        var input = uiUpdateLoopInput as Input ?? throw new InvalidDataException("Invalid input for UiLoopDiagram");

        var currentTime = stopwatch.Elapsed;
        if (audioLibWrapper.PlayState != SoundFlow.Enums.PlaybackState.Playing)
            return;
        var movement = (currentTime! - titleLastUpdateTime!).Value.Milliseconds / 69.0;

        if (input.titleCanvas!.Bounds.Width > titleCanvasText1!.DesiredSize.Width)
        {
            input.titleCanvas.OpacityMask = null;
            titleCanvasText1X = 0;
            titleCanvasText2X = -9999;

            titleLastPlainTitleTime = frameCounter;
        }
        else
        {
            titleCanvasText1X -= movement;
            titleCanvasText2X = titleCanvasText1X + titleCanvasText1Width + titleGap;
            if (titleCanvasText1X + titleCanvasText1Width < 0)
            {
                titleCanvasText1X = titleCanvasText2X;
                titleCanvasText2X = titleCanvasText1X + titleCanvasText1Width + titleGap;
            }

            input.titleCanvas.OpacityMask = titleInitialOpacityMask;
            var timeSinceLastPlainTitle = frameCounter - titleLastPlainTitleTime;
            if (timeSinceLastPlainTitle < 300)
            {
                titleInitialOpacityMask?.StartPoint = new RelativePoint(titleInverseOpacityMaskAnimSpeed / -timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
                titleInitialOpacityMask?.EndPoint = new RelativePoint(1 + titleInverseOpacityMaskAnimSpeed / timeSinceLastPlainTitle, 0.5, RelativeUnit.Relative);
            }
        }

        Canvas.SetLeft(titleCanvasText1, titleCanvasText1X!.Value);
        Canvas.SetLeft(titleCanvasText2!, titleCanvasText2X!.Value);

        titleLastUpdateTime = stopwatch.Elapsed;
    }

    public record UpdateTitleEventArgs(string newTitle);
    public override List<IUiUpdateLoopEventHandler>? Events
    {
        get =>
        [
            new UiUpdateLoopEventHandler<UpdateTitleEventArgs>((args, uiUpdateLoopInput) =>
                {
                    var input = uiUpdateLoopInput as Input ?? throw new InvalidDataException("Invalid input for UiLoopDiagram");

                    titleCanvasText1 = input.titleCanvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText1")!;
                    titleCanvasText2 = input.titleCanvas.Children.OfType<TextBlock>().FirstOrDefault(x => x.Name == "TitleCanvasText2")!;

                    titleCanvasText1.Text = args.newTitle;
                    titleCanvasText2.Text = args.newTitle;

                    titleCanvasText1.Measure(Size.Infinity);
                    titleCanvasText1Width = titleCanvasText1.DesiredSize.Width;
                }
            )
        ];
    }
}