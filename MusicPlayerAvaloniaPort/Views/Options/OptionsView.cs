using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Song;

namespace MusicPlayerAvaloniaPort.Views.Options;

public partial class OptionsView : UserControl
{
    Window? window => TopLevel.GetTopLevel(this) as Window;

    readonly SongSyncService syncService = ServiceContainer.GetService<SongSyncService>();

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

            window.MinWidth = window.Width;
            window.MinHeight = window.Height;
        });

        var stateLabel = this.GetNestedControl<Label>("stateLabel");
        stateLabel?.Content = syncService.State;
        syncService.OnStateChanged = state => Dispatcher.Invoke(() => stateLabel?.Content = state);
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