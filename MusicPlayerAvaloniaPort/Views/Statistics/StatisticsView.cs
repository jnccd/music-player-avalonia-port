using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.ViewModels;
using Avalonia.Collections;

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

        this.AddHandler(
            InputElement.KeyDownEvent,
            StatisticsView_KeyDown,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
    }

    private void StatisticsView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S)
        {
            Task.Run(() =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    var searchString = await new MessageBox((e) => { }, window, this).GetTextAsync("Search");
                    SearchSort(searchString);
                });
            });
        }

        if (e.Key == Key.J)
        {
            var grid = this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");
            var currentlyPlaying = songPlaybackService.CurrentlyPlaying;
            var currentyPlayingVM = viewModel?.StatisticsSongVMs.FirstOrDefault(x => x.SongId == currentlyPlaying?.UpvotedSongId);
            grid?.ScrollIntoView(currentyPlayingVM, null);
            grid?.SelectedItem = currentyPlayingVM;
        }
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

        SearchSort(searchString);
    }

    private void searchTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        SearchButton_Click(sender, new RoutedEventArgs());
    }

    void SearchSort(string searchString)
    {
        var grid = this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");

        grid?.CollectionView.Refresh();

        viewModel?.StatisticsSongVMs.Clear();
        var songs = StatisticsViewModel.GetSongs();

        var searchSortedSongs = songs.OrderBy(s => HelperFuncs.LevenshteinDistanceWrapper(searchString, s.Name));

        foreach (var song in searchSortedSongs)
            viewModel?.StatisticsSongVMs.Add(song);

        grid?.CollectionView.Refresh();
    }
}