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
using System.Collections.Generic;
using MusicPlayerAvaloniaPort.Services.Infrastructure;

namespace MusicPlayerAvaloniaPort.Views.Statistics;

public partial class StatisticsView : UserControl
{
    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    readonly DbWrapperService dbWrapper = ServiceContainer.GetService<DbWrapperService>();

    Window? window => TopLevel.GetTopLevel(this) as Window;
    StatisticsViewModel? viewModel => DataContext as StatisticsViewModel;
    DateTime lastStatisticsView_KeyDownTime = DateTime.MinValue;

    public StatisticsView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);

        // Events
        this.Loaded += StatisticsView_Loaded;
    }

    private async void StatisticsView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("StatisticsView loaded!");

        this.AddHandler(
            InputElement.KeyDownEvent,
            StatisticsView_KeyDown,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true
        );

        await SetupUi();
    }

    private async Task SetupUi()
    {
        viewModel!.StatisticsSongVMs.Clear();
        var songs = await GetSongs();
        foreach (var song in songs.OrderByDescending(song => song.Score))
        {
            viewModel.StatisticsSongVMs.Add(song);
        }
    }

    private void StatisticsView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (lastStatisticsView_KeyDownTime.AddMilliseconds(500) > DateTime.Now)
            return;

        if (e.Key == Key.S)
        {
            Task.Run(() =>
            {
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var searchString = await new MessageBox((e) => { }, window, this).GetTextAsync("Search");
                    await SearchSort(searchString);
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

        lastStatisticsView_KeyDownTime = DateTime.Now;
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

    private async void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        var searchTextBox = this.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(x => x.Name == "searchTextBox");
        var searchString = searchTextBox?.Text;
        if (searchString == null)
            return;

        await SearchSort(searchString);
    }

    private void searchTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        SearchButton_Click(sender, new RoutedEventArgs());
    }

    public async Task<IEnumerable<StatisticsSongViewModel>> GetSongs() =>
        await Task.Run(() =>
            dbWrapper?
                .GetContext()
                .DumpUpvotedSongs()
                .Select(song => new StatisticsSongViewModel(song))
            ?? []);

    async Task SearchSort(string searchString)
    {
        var grid = this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");

        grid?.CollectionView.Refresh();

        viewModel?.StatisticsSongVMs.Clear();
        var songs = await GetSongs();

        var searchSortedSongs = songs.OrderBy(s => HelperFuncs.LevenshteinDistanceWrapper(searchString, s.Name));

        foreach (var song in searchSortedSongs)
            viewModel?.StatisticsSongVMs.Add(song);

        grid?.CollectionView.Refresh();
    }
}