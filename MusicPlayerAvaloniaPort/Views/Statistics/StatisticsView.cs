using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.ViewModels;

namespace MusicPlayerAvaloniaPort.Views.Statistics;

public partial class StatisticsView : UserControl
{
    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    Window? window => TopLevel.GetTopLevel(this) as Window;
    StatisticsViewModel? viewModel => DataContext as StatisticsViewModel;

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

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");

        if (grid?.SelectedItem is not StatisticsSongViewModel song)
            return;

        var availableSong = songPlaybackService.FindAvailableSong(song.SongId);

        if (availableSong == null)
            return;

        songPlaybackService.PlaySpecificSong(availableSong);
    }

    private void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        var searchTextBox = this.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(x => x.Name == "searchTextBox");
        var searchString = searchTextBox?.Text;
        if (searchString == null)
            return;

        var grid = this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");
        grid?.CanUserSortColumns = string.IsNullOrWhiteSpace(searchString);

        viewModel?.StatisticsSongVMs.Clear();
        var songs = StatisticsViewModel.GetSongs();

        var searchSortedSongs = songs.OrderBy(s => HelperFuncs.LevenshteinDistanceWrapper(searchString, s.Name));

        foreach (var song in searchSortedSongs)
            viewModel?.StatisticsSongVMs.Add(song);
    }

    private void searchTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        SearchButton_Click(sender, new RoutedEventArgs());
    }
}