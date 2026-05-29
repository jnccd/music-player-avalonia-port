using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace MusicPlayerAvaloniaPort.Views.OptionsView;

public partial class OptionsView : UserControl
{
    Window? window => this.GetVisualRoot() as Window;

    public OptionsView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        window?.AttachDevTools();
#endif

        // Events
        this.Loaded += OptionsView_Loaded;
    }

    private void OptionsView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("OptionsView loaded!");
    }
}