using System.IO;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.UiUpdateLoop;

namespace MusicPlayerAvaloniaPort.Views.Main;

[RegisterUiLoop]
public class UiLoopPlayProgress() : IUiUpdateLoop(typeof(MainView), typeof(Input))
{
    public record Input(
        Rectangle? DurationBarBackRectangle,
        Rectangle? DurationBarAntiAliasingRectangle,
        Rectangle? DurationBarDurationRectangle,
        SolidColorBrush? PrimaryColorBrush,
        double PixelScale = 0.5) : IUiUpdateLoopInput;
    readonly AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();

    SolidColorBrush? AntiAliasRectBrush;

    public override void Init(IUiUpdateLoopInput uiUpdateLoopInput)
    {
        var input = uiUpdateLoopInput as Input ?? throw new InvalidDataException("Invalid input for UiLoopDiagram");

        AntiAliasRectBrush = new SolidColorBrush(input.PrimaryColorBrush!.Color, input.PrimaryColorBrush.Opacity);
    }

    public override void Update(IUiUpdateLoopInput uiUpdateLoopInput, ulong frameCounter)
    {
        var input = uiUpdateLoopInput as Input ?? throw new InvalidDataException("Invalid input for UiLoopDiagram");

        var pixelProgress = input.DurationBarBackRectangle?.Bounds.Width * audioLibWrapper.PlayProgress ?? 0;
        input.DurationBarDurationRectangle?.Width = pixelProgress - input.PixelScale > 0 ? pixelProgress - input.PixelScale : 0;
        input.DurationBarAntiAliasingRectangle?.Width = pixelProgress;
        AntiAliasRectBrush?.Opacity = pixelProgress % input.PixelScale;
    }
}