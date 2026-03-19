using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace MusicPlayerAvaloniaPort;

public partial class MainView : UserControl
{
    Rectangle? durationBarRectangle;
    Rectangle? durationBarDurationRectangle;

    private void InitPlayProgressUpdater()
    {
        durationBarRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarRectangle")!;
        durationBarDurationRectangle = this.GetLogicalDescendants().OfType<Rectangle>().FirstOrDefault(x => x.Name == "DurationBarDurationRectangle")!;
    }

    private void DoPlayProgressUpdate()
    {
        durationBarDurationRectangle?.Width = durationBarRectangle?.Bounds.Width * audioLibWrapper.PlayProgress ?? 0;
    }
}