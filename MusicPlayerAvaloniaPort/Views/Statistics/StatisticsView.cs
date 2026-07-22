using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;

namespace MusicPlayerAvaloniaPort.Views.Statistics;

public partial class StatisticsView : UserControl
{
    Window? window => TopLevel.GetTopLevel(this) as Window;

    public StatisticsView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);

        // Events
        this.Loaded += StatisticsView_Loaded;
    }

    private void StatisticsView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("StatisticsView loaded!");


    }

    private void UwU_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");

        var uwu = grid.SelectedItems;
    }
}