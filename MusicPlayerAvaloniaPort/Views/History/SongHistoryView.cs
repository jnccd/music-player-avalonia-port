using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.ViewModels;

namespace MusicPlayerAvaloniaPort.Views.History;

/// <summary>
/// The song history: every recorded listening event (a vote that changed a song's score) with its date and
/// score change, newest first - the port of the DxMGP history window. Double clicking a row plays that
/// song, like the DxMGP window did.
/// </summary>
public partial class SongHistoryView : UserControl
{
    readonly DbWrapperService dbWrapper = ServiceContainer.GetService<DbWrapperService>();
    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();

    SongHistoryViewModel? viewModel => DataContext as SongHistoryViewModel;
    Window? window => TopLevel.GetTopLevel(this) as Window;
    DataGrid? dataGrid => this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");
    TextBox? searchBox => this.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(x => x.Name == "searchTextBox");
    TextBlock? countLabel => this.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "entryCountText");
    SongHistoryItemViewModel? SelectedEntry => dataGrid?.SelectedItem as SongHistoryItemViewModel;
    MessageBox GetMessageBox() => new((ex) => Console.WriteLine(ex), window, this);

    /// <summary>The complete history as loaded from the database; a search only filters this list.</summary>
    List<SongHistoryItemViewModel> allEntries = [];
    string? appliedSearchText;

    public SongHistoryView()
    {
        AvaloniaXamlLoader.Load(this);
        this.Loaded += SongHistoryView_Loaded;
    }

    private async void SongHistoryView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("SongHistoryView loaded!");
        await ReloadAsync();
    }

    // ---------- Loading / searching ----------

    async Task ReloadAsync()
    {
        // Reading the history (and the song names it refers to) can be tens of thousands of rows, so it
        // happens off the UI thread.
        List<SongHistoryItemViewModel> entries = await Task.Run(BuildEntries);
        allEntries = entries;
        appliedSearchText = null;

        var searchBoxControl = searchBox;
        if (searchBoxControl != null)
            searchBoxControl.Text = "";

        RepopulateHistory();
    }

    List<SongHistoryItemViewModel> BuildEntries()
    {
        using var dbContext = dbWrapper.GetContext();

        var songNamesById = dbContext
            .DumpUpvotedSongs()
            .GroupBy(song => song.SongId)
            .ToDictionary(group => group.Key, group => Path.GetFileNameWithoutExtension(group.First().Name));

        return [.. dbContext
            .DumpSongHistory()
            .OrderByDescending(entry => entry.Date)
            .Select(entry => new SongHistoryItemViewModel(
                entry.SongId,
                entry.SongId is Guid songId && songNamesById.TryGetValue(songId, out string? name) ? name : "",
                entry.Date,
                entry.ScoreChange))];
    }

    void RepopulateHistory()
    {
        IEnumerable<SongHistoryItemViewModel> shown = allEntries;
        if (!string.IsNullOrEmpty(appliedSearchText))
            shown = shown.Where(entry => entry.SongName.Contains(appliedSearchText, StringComparison.OrdinalIgnoreCase));

        List<SongHistoryItemViewModel> rows = [.. shown];
        if (viewModel != null)
            viewModel.Entries = rows;

        if (countLabel != null)
            countLabel.Text = string.IsNullOrEmpty(appliedSearchText)
                ? $"{rows.Count} entries"
                : $"{rows.Count} of {allEntries.Count} entries";

        // A column sort the DataGrid is holding is re-applied to the fresh rows here.
        dataGrid?.CollectionView.Refresh();
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e) => await ReloadAsync();

    private void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        string searchText = (searchBox?.Text ?? "").Trim();
        appliedSearchText = searchText.Length == 0 ? null : searchText;

        // A search shows the raw (date-ordered) list, so any column sort is dropped first - the text stays
        // in the box, hitting Search again simply re-applies it.
        var grid = dataGrid;
        if (grid?.CollectionView.SortDescriptions is { Count: > 0 })
        {
            using (grid.CollectionView.DeferRefresh())
                grid.CollectionView.SortDescriptions.Clear();
        }

        RepopulateHistory();
    }

    private void SearchTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        SearchButton_Click(sender, new RoutedEventArgs());
    }

    // ---------- Context menu / double click ----------

    private void DataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        Play_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not { SongId: Guid songId })
            return;

        var availableSong = songPlaybackService.FindAvailableSong(songId);
        if (availableSong == null)
        {
            GetMessageBox().Show("Play failed", "This entry isnt linked to a song file in the music library!");
            return;
        }

        songPlaybackService.PlaySpecificSong(availableSong);
    }

    private void OpenInExplorer_Click(object? sender, RoutedEventArgs e)
    {
        string? filePath = ResolveSelectedFilePath();
        if (filePath == null)
        {
            GetMessageBox().Show("Open in Explorer failed", "This entry isnt linked to a song file in the music library!");
            return;
        }

        if (!PlatformShell.RevealFileInFileManager(filePath))
            GetMessageBox().Show("Open in Explorer failed", "Could not open the file manager.");
    }

    private async void CopyTitle_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not { HasSongFile: true } entry)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(entry.SongName);
    }

    string? ResolveSelectedFilePath()
    {
        if (SelectedEntry is not { SongId: Guid songId })
            return null;

        string? filePath = songPlaybackService.FindAvailableSong(songId)?.FilePath;
        return filePath != null && File.Exists(filePath) ? filePath : null;
    }
}
