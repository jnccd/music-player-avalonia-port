using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPort.ViewModels;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.Views.Statistics;

public partial class StatisticsView : UserControl
{
    readonly SongPlaybackService songPlaybackService = ServiceContainer.GetService<SongPlaybackService>();
    readonly DbWrapperService dbWrapper = ServiceContainer.GetService<DbWrapperService>();

    Window? window => TopLevel.GetTopLevel(this) as Window;
    StatisticsViewModel? viewModel => DataContext as StatisticsViewModel;

    DataGrid? dataGrid => this.GetLogicalDescendants().OfType<DataGrid>().FirstOrDefault(x => x.Name == "DataGrid");

    // In-memory snapshot of the songs loaded from the DB. Search/reload reorder this list instead of
    // hitting the database again and again (a per-keystroke DB reload is what made the grid feel dead).
    readonly List<StatisticsSongViewModel> allSongs = [];
    // The search text that is currently applied (null = no search active -> default score order).
    // Searching only reorders the list (like the DxMGP port), it doesn't filter rows out.
    string? appliedSearchText;

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
        await ReloadSongsAsync();
    }

    // ---------- Loading / ordering ----------

    async Task ReloadSongsAsync()
    {
        var songs = await GetSongsAsync();
        allSongs.Clear();
        allSongs.AddRange(songs);
        RepopulateSongList();
    }

    void RepopulateSongList()
    {
        IEnumerable<StatisticsSongViewModel> orderedSongs =
            !string.IsNullOrEmpty(appliedSearchText)
                ? allSongs.OrderBy(song => HelperFuncs.LevenshteinDistanceWrapper(appliedSearchText, song.Name))
                : allSongs.OrderByDescending(song => song.Score);

        viewModel?.StatisticsSongVMs.Clear();
        foreach (var song in orderedSongs)
            viewModel?.StatisticsSongVMs.Add(song);

        // If a column sort is active the DataGrid keeps its SortDescriptions across reloads and re-applies
        // them to the fresh rows here. Without an active sort this simply shows the order filled in above.
        dataGrid?.CollectionView.Refresh();
    }

    public async Task<List<StatisticsSongViewModel>> GetSongsAsync() =>
        await Task.Run(() =>
            (dbWrapper?
                .GetContext()
                .DumpUpvotedSongs()
                .Select(song => new StatisticsSongViewModel(song))
            ?? []).ToList());

    // ---------- Toolbar ----------

    // Reloads the data from the database. Any active ordering (search or column sort) is kept,
    // like the Refresh button in the DxMGP port re-applied the last sort.
    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        await ReloadSongsAsync();
    }

    // Selects and scrolls to the row of the song that is currently playing.
    private void ToPlayingButton_Click(object? sender, RoutedEventArgs e)
    {
        var grid = dataGrid;
        var currentlyPlaying = songPlaybackService.CurrentlyPlaying;
        var currentlyPlayingVM = viewModel?.StatisticsSongVMs.FirstOrDefault(x => x.SongId == currentlyPlaying?.UpvotedSongId);
        if (grid == null || currentlyPlayingVM == null)
            return;

        grid.ScrollIntoView(currentlyPlayingVM, null);
        grid.SelectedItem = currentlyPlayingVM;
    }

    private async void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        var searchTextBox = this.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(x => x.Name == "searchTextBox");
        var searchString = searchTextBox?.Text;
        if (searchString == null)
            return;

        await ApplySearchAsync(searchString);
    }

    private void searchTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        SearchButton_Click(sender, new RoutedEventArgs());
    }

    // Called when a column header is clicked: the DataGrid applies its own sort (ascending/descending).
    // That sort replaces an applied search ordering - the text stays in the box, so hitting Search
    // again simply re-applies the search.
    private void DataGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        appliedSearchText = null;
    }

    async Task ApplySearchAsync(string searchString)
    {
        searchString = searchString.Trim();
        appliedSearchText = searchString.Length == 0 ? null : searchString;

        // A search (or an empty search = reset to default order) shows the raw collection order, so any
        // column sort the DataGrid is currently holding has to be dropped first.
        var grid = dataGrid;
        if (grid?.CollectionView.SortDescriptions is { Count: > 0 })
        {
            using (grid.CollectionView.DeferRefresh())
                grid.CollectionView.SortDescriptions.Clear();
        }

        RepopulateSongList();
    }

    // ---------- Context menu ----------

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (dataGrid?.SelectedItem is not StatisticsSongViewModel song)
            return;

        var availableSong = songPlaybackService.FindAvailableSong(song.SongId);

        if (availableSong == null)
            return;

        songPlaybackService.PlaySpecificSong(availableSong);
    }

    private async void Rename_Click(object? sender, RoutedEventArgs e)
    {
        MessageBox GetMessageBox() => new((ex) => Console.WriteLine(ex), window, this);

        if (dataGrid?.SelectedItem is not StatisticsSongViewModel song)
            return;

        var syncService = ServiceContainer.GetService<SongSyncService>();

        var availableSong = songPlaybackService.FindAvailableSong(song.SongId);
        if (availableSong == null)
        {
            GetMessageBox().Show("Cant rename song", "This entry isnt linked to a song file in the music library!");
            return;
        }

        if (songPlaybackService.CurrentlyPlaying?.UpvotedSongId == song.SongId)
        {
            GetMessageBox().Show("Cant rename song", "You cant play a file and rename it at the same time!");
            return;
        }

        // Resolve the database entry (its Name is the file name including the extension)
        UpvotedSong? upvotedSong;
        using (var dbContext = dbWrapper.GetContext())
        {
            upvotedSong = dbContext.DumpUpvotedSongs().FirstOrDefault(x => x.SongId == song.SongId);
        }
        if (upvotedSong == null)
        {
            GetMessageBox().Show("Cant rename song", "The database entry for this song was not found anymore!");
            return;
        }

        string oldName = upvotedSong.Name;
        string oldExtension = Path.GetExtension(oldName);

        var newTitle = await GetMessageBox().GetTextAsync($"What name should \"{Path.GetFileNameWithoutExtension(oldName)}\" get?");
        if (string.IsNullOrWhiteSpace(newTitle))
            return;
        newTitle = newTitle.Trim();

        if (newTitle == Path.GetFileNameWithoutExtension(oldName))
        {
            GetMessageBox().Show("Cant rename song", "You didn't change the name...");
            return;
        }
        if (newTitle.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newTitle.Contains('/') || newTitle.Contains('\\'))
        {
            GetMessageBox().Show("Cant rename song", $"\"{newTitle}\" contains invalid characters!");
            return;
        }

        string newName = Path.HasExtension(newTitle) ? newTitle : newTitle + oldExtension;
        if (newName == oldName)
        {
            GetMessageBox().Show("Cant rename song", "You didn't change the name...");
            return;
        }

        // A migration refers to one specific upvotedSong entry (its SongId) - the row the user clicked on.
        // Only rename files whose tags match this entry (a file with the same file name but different
        // album/artist tags is a different song). Entries without album/artist metadata (legacy entries)
        // can only be identified by their file name. This runs before the commit point POST, so no
        // migration is created for a rename that cannot be applied locally.
        if (upvotedSong.Name != oldName)
        {
            GetMessageBox().Show("Cant rename song", "The song entry changed in the meantime, please try again!");
            return;
        }
        Guid targetSongId = upvotedSong.SongId;

        var filesToRename = new List<string>();
        int skippedCopies = 0;
        var candidates = Config.Data.SongLibraryPath != null
            ? SongSyncService.FindSongFilesByName(Config.Data.SongLibraryPath, oldName)
            : new List<string>();
        if (string.IsNullOrWhiteSpace(upvotedSong.Artist) && string.IsNullOrWhiteSpace(upvotedSong.Album))
        {
            // The entry carries no album/artist info: the file name is all the identity it has.
            filesToRename.AddRange(candidates);
        }
        else
        {
            foreach (string candidate in candidates)
            {
                if (SongSyncService.SongFileMatchesEntry(candidate, upvotedSong))
                    filesToRename.Add(candidate);
                else
                    skippedCopies++;
            }
        }
        if (filesToRename.Count == 0)
        {
            GetMessageBox().Show("Cant rename song",
                "No file of this song could be found in the song library.\n\n" +
                (skippedCopies > 0
                    ? "Files with this name exist, but their album/artist metadata does not match this song entry, so they were not renamed."
                    : "Files with this name exist, but they could not be read or matched to this song entry."));
            return;
        }

        foreach (string oldFilePath in filesToRename)
        {
            string collidingTargetPath = Path.Combine(Path.GetDirectoryName(oldFilePath) ?? "", newName);
            if (File.Exists(collidingTargetPath) && File.Exists(oldFilePath))
            {
                GetMessageBox().Show("Cant rename song", $"A file called \"{newName}\" already exists in the song library!");
                return;
            }
        }

        // Commit point: the migration POST on the server. The server assigns the migration number and
        // renames the entry with the given SongId. If this fails, abort without changing anything locally,
        // since migrations should only be done with a working server connection.
        var createdMigration = await Task.Run(() =>
            syncService.PostSongLibraryMigration(new SongLibraryMigration(oldName, newName, SongLibraryMigrationType.Rename)
            {
                SongId = targetSongId
            }));

        if (createdMigration == null)
        {
            GetMessageBox().Show("Rename aborted", "The sync server did not accept the rename.\n\n" + syncService.State +
                "\n\n(Songs can only be renamed while the connection to the sync server is up and their entry was synced)");
            return;
        }

        // Rename every copy of the file in the song library (there can be copies in multiple subfolders).
        // If a copy is already gone and its target already exists, another client (e.g. sharing the library
        // via NAS) already renamed it - that counts as done. Only if all renames went through is the
        // migration state bumped, so a failed rename is going to be retried automatically on the next sync.
        var renamedFiles = new List<(string OldPath, string NewPath)>();
        try
        {
            foreach (string oldFilePath in filesToRename)
            {
                string newFilePath = Path.Combine(Path.GetDirectoryName(oldFilePath) ?? "", newName);
                if (File.Exists(newFilePath))
                {
                    if (File.Exists(oldFilePath))
                    {
                        // A different file with the target name is in the way (appeared since the pre-check).
                        RollbackFileRenames(renamedFiles);
                        GetMessageBox().Show("Cant rename song", $"A file called \"{newName}\" already exists in the song library!");
                        return;
                    }
                    renamedFiles.Add((oldFilePath, newFilePath)); // Another client already renamed this copy
                    continue;
                }
                File.Move(oldFilePath, newFilePath);
                renamedFiles.Add((oldFilePath, newFilePath));
            }
        }
        catch (Exception ex)
        {
            // The migration is already on the server, but the library migration state was not bumped, so the
            // remaining renames are going to be retried automatically on the next sync.
            GetMessageBox().Show("Rename incomplete", $"The server registered the rename, but some files could not be renamed locally:\n{ex.Message}");
            return;
        }

        // Update the local database entry (the server already renamed its copy of the row), plus any queued
        // "/sync/new-song" uploads of the affected song, so a retried upload creates the row under the new
        // name instead of resurrecting the old one.
        try
        {
            using var dbContext = dbWrapper.GetContext();
            var songIdsToRename = new[] { targetSongId };
            var rowsToRename = dbContext.DumpUpvotedSongs().Where(x => x.SongId == targetSongId).ToArray();
            foreach (var rowToRename in rowsToRename)
            {
                if (dbContext.DumpUpvotedSongs().Any(x => x.SongId != rowToRename.SongId && x.Name == newName && x.Artist == rowToRename.Artist && x.Album == rowToRename.Album))
                {
                    // Target name already taken by a different entry, roll the file renames back.
                    RollbackFileRenames(renamedFiles);
                    GetMessageBox().Show("Cant rename song", "A song with that name already exists!");
                    return;
                }
                rowToRename.Name = newName;
            }
            dbContext.RenameQueuedSongUploads(songIdsToRename, newName);
            dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            RollbackFileRenames(renamedFiles);
            GetMessageBox().Show("Cant rename song", $"Could not update the local database:\n{ex.Message}");
            return;
        }

        // Update the in-memory available songs, play history and choosing list (the files moved)
        songPlaybackService.RenameSongFiles(renamedFiles);

        // Remember the migration state so it is not applied again on the next sync. The migration response
        // carries the account user id, which is recorded in the library config file as the library owner.
        if (Config.Data.SongLibraryPath != null)
            syncService.WriteSongLibraryMigrationState(Config.Data.SongLibraryPath, createdMigration.UserId, createdMigration.MigrationNumber);

        // If the library turned out to be registered for a different account, warn about it
        var ownerWarning = syncService.TakeSongLibraryOwnerWarning();
        if (ownerWarning != null)
            GetMessageBox().Show("Song library account warning", ownerWarning);

        // Refresh the statistics list (row name etc.), keeping the active search/column sort
        await ReloadSongsAsync();

        GetMessageBox().Show("Rename successful", $"Successfully renamed \"{oldName}\" to \"{newName}\"!" + (skippedCopies > 0
            ? $"\n\nNote: {skippedCopies} file(s) with the old name were left alone, since their album/artist metadata did not match this song entry."
            : ""));
    }

    static void RollbackFileRenames(List<(string OldPath, string NewPath)> renamedFiles)
    {
        for (int i = renamedFiles.Count - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(renamedFiles[i].NewPath) && !File.Exists(renamedFiles[i].OldPath))
                    File.Move(renamedFiles[i].NewPath, renamedFiles[i].OldPath);
            }
            catch { }
        }
    }
}
