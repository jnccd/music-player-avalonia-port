using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPortMobile.ViewModels;
using System;

namespace MusicPlayerAvaloniaPortMobile.Views;

/// <summary>
/// The single view of the mobile client. All behaviour lives in <see cref="MobileMainViewModel"/> (which in
/// turn drives the shared song services), so the code behind is nothing but the button wiring.
/// </summary>
public partial class MobileMainView : UserControl
{
    MobileMainViewModel? ViewModel => DataContext as MobileMainViewModel;

    public MobileMainView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += (_, _) => ViewModel?.StartStartup();
    }

    void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            // Refresh the "where music was looked for" list, so the sheet always shows the current state.
            viewModel.RefreshLibraryDiagnostics();
            viewModel.SettingsOpen = true;
        }
    }

    void CloseSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            viewModel.SettingsOpen = false;
    }

    void PlayPauseButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.TogglePlayPause());

    void NextButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.NextSong());

    void PreviousButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.PreviousSong());

    void UpvoteButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.ToggleUpvote());

    async void LoginButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.LoginAndSyncAsync();
    }

    async void LoginWithoutUploadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.LoginWithoutUploadAsync();
    }

    void LogoutButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.Logout());

    void ClearSearchButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.ClearSearch());

    /// <summary>
    /// Plays the song of the tapped suggestion. The row is a Button whose DataContext is the
    /// <see cref="MobileSearchResult"/>, so the item does not need a command or a binding back to the view.
    /// </summary>
    void SearchResult_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MobileSearchResult result })
            Run(viewModel => viewModel.PlaySearchResult(result));
    }

    void RegisterButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.OpenRegistrationPage());

    async void RescanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.RescanLibraryAsync();
    }

    async void UseLibraryFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.UseEnteredLibraryFolderAsync();
    }

    void AdoptLibraryButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.AdoptSongLibrary());

    void DismissOwnerWarningButton_Click(object? sender, RoutedEventArgs e) => Run(viewModel => viewModel.DismissLibraryOwnerWarning());

    /// <summary>
    /// Runs one user action. Exceptions are surfaced in the status line instead of tearing the app down:
    /// a phone player must not close because starting one song failed (e.g. a file vanished).
    /// </summary>
    void Run(Action<MobileMainViewModel> action)
    {
        if (ViewModel is not { } viewModel)
            return;

        try
        {
            action(viewModel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mobile action failed: {ex}");
        }
    }
}
