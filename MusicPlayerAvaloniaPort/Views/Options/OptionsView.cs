using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Infrastructure;

namespace MusicPlayerAvaloniaPort.Views.Options;

public partial class OptionsView : UserControl
{
    Window? window => TopLevel.GetTopLevel(this) as Window;

    readonly SongSyncService syncService = ServiceContainer.GetService<SongSyncService>();
    readonly SongDownloadRequestProcessorService songDownloadRequestProcessorService = ServiceContainer.GetService<SongDownloadRequestProcessorService>();

    public OptionsView()
    {
        // Avalonia Init
        AvaloniaXamlLoader.Load(this);

        // Events
        this.Loaded += OptionsView_Loaded;
    }

    private void OptionsView_Loaded(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("OptionsView loaded!");

        Dispatcher.Invoke(() =>
        {
            if (window == null)
                throw new InvalidDataException(nameof(window));

            window.MinWidth = double.IsNormal(window.Width) ? window.Width : 0;
            window.MinHeight = double.IsNormal(window.Height) ? window.Height : 0;
        });

        var stateLabel = this.GetNestedControl<TextBlock>("stateLabel");
        stateLabel?.Text = syncService.State;
        syncService.OnStateChanged = state => Dispatcher.Invoke(() => stateLabel?.Text = state);
    }

    private void DownloadFolderSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var downloadFolderTextBox = this.GetNestedControl<TextBox>("downloadFolderTextBox");
        if (!Directory.Exists(downloadFolderTextBox.Text))
        {
            new MessageBox(_ => { }, window, this).Show("Invalid folder path", $"{downloadFolderTextBox.Text} doesn't exist!");
            return;
        }
        Config.Data.DownloadFolderPath = downloadFolderTextBox.Text;

        songDownloadRequestProcessorService.Init();
        Config.Save();
    }

    private void LoginButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var textBoxHost = this.GetNestedControl<TextBox>("hostTextBox");
            var textBoxUsername = this.GetNestedControl<TextBox>("usernameTextBox");
            var textBoxPassword = this.GetNestedControl<TextBox>("passwordTextBox");

            Config.Data.SyncServerHost = textBoxHost.Text;
            Config.Data.SyncServerUsername = textBoxUsername.Text;
            Config.Save();

            syncService.Init(textBoxPassword.Text, true);
        }
        catch (Exception ex)
        {
            new MessageBox(e => Console.WriteLine(e), window, this)
                .Show("Can't initialize login.", $"{syncService.State}\n\n{ex}");
            return;
        }

        try
        {
            syncService.Pull();
        }
        catch (Exception ex)
        {
            new MessageBox(e => Console.WriteLine(e), window, this)
                .Show("Can't pull.", $"{syncService.State}\n\n{ex}");
            return;
        }
    }

    private void RegisterButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var textBoxHost = this.GetNestedControl<TextBox>("hostTextBox");

            var url = syncService.GetAccountRegistrationAddress(textBoxHost.Text);
            window!.OpenUrlOnCurrentOsBrowser(url);
        }
        catch (Exception ex)
        {
            new MessageBox(e => Console.WriteLine(e), window, this)
                .Show("Can't open registration.", $"{syncService.State}\n\n{ex}");
        }
    }
}