using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Services.Infrastructure;

namespace MusicPlayerAvaloniaPort.Views.Main;

public class CustomRenderControl_PlayProgress : Control
{
    readonly AudioLibWrapperService audioLibWrapper = ServiceContainer.GetService<AudioLibWrapperService>();
    Window? window => TopLevel.GetTopLevel(this) as Window;
    UserControl? view => window?.Content as UserControl;

    SolidColorBrush? PrimaryColorBrush;
    SolidColorBrush? AntiAliasRectBrush;
    SolidColorBrush? WhiteBrush = new(Colors.White);
    double PixelScale;

    public CustomRenderControl_PlayProgress() : base()
    {
        this.Loaded += (s, e) =>
        {
            Debug.WriteLine("CustomRenderControl_PlayProgress loaded!");
            PrimaryColorBrush = view!.FindResource("PrimaryColor") as SolidColorBrush;
            AntiAliasRectBrush = new SolidColorBrush(PrimaryColorBrush!.Color, PrimaryColorBrush.Opacity);

            PixelScale = 1 / window!.RenderScaling;
        };
    }

    public override void Render(DrawingContext context)
    {
        Program.WrapInTry(() =>
        {
            base.Render(context);
            Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);

            Update();
            Draw(context);
        });
    }

    public void Update()
    {

    }

    private void Draw(DrawingContext context)
    {
        context.DrawRectangle(WhiteBrush, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
        var pixelProgress = Bounds.Width * audioLibWrapper.PlayProgress ?? 0;
        context.DrawRectangle(PrimaryColorBrush, null, new Rect(0, 0, pixelProgress, Bounds.Height));
        AntiAliasRectBrush?.Opacity = pixelProgress % PixelScale;
        context.DrawRectangle(AntiAliasRectBrush, null, new Rect(pixelProgress, 0, PixelScale, Bounds.Height));
    }
}