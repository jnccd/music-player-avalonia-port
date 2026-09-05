using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Helpers;

public class MessageBox(Action<Exception>? OnError, Window? OriginWindow, Control? FlyoutOrigin)
{
    Window? currentWindow;
    Flyout? currentFlyout;

    public void Show(string title, string message, bool AlwaysAsFlyout = false, bool TakeFocus = true,
        double width = 400, double height = 115)
    {
        try
        {
            if (Globals.IsDesktop && !AlwaysAsFlyout)
            {
                ShowPopupWindow(title, message, TakeFocus, width, height);
            }
            else
            {
                ShowPopupFlyout(title, message);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
    }

    /// <summary>
    /// Like <see cref="Show"/> but opens the message box as a modal dialog of the origin window and only
    /// returns once the user dismissed it (OK or window close).
    /// </summary>
    public async Task ShowAsync(string title, string message, bool AlwaysAsFlyout = false, bool TakeFocus = true,
        double width = 400, double height = 115)
    {
        try
        {
            if (Globals.IsDesktop && !AlwaysAsFlyout)
            {
                await ShowPopupWindowAsync(title, message, TakeFocus, width, height);
            }
            else
            {
                ShowPopupFlyout(title, message);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
    }

    public async Task<string> GetTextAsync(string title, bool AlwaysAsFlyout = false, bool TakeFocus = true)
    {
        try
        {
            if (Globals.IsDesktop && !AlwaysAsFlyout)
            {
                return await ShowTextInputWindowAsync(title, TakeFocus);
            }
            else
            {
                throw new NotImplementedException();
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            return "";
        }
    }

    public async Task<bool> AskYesNoAsync(string title, string message, bool AlwaysAsFlyout = false, bool TakeFocus = true)
    {
        try
        {
            if (Globals.IsDesktop && !AlwaysAsFlyout)
            {
                return await ShowYesNoWindowAsync(title, message, TakeFocus);
            }
            else
            {
                throw new NotImplementedException();
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            return false;
        }
    }

    private async Task<string> ShowTextInputWindowAsync(string title, bool TakeFocus = true)
    {
        var tcs = new TaskCompletionSource<string>();

        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, // Centers text horizontally
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,     // Centers text vertically
            Width = 120,
            Height = 30
        };
        var textBox = new TextBox
        {
            Text = "",
        };
        var grid = new Grid
        {
            Margin = new Thickness(10),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = new GridLength(40) },
            }
        };
        grid.Children.Add(textBox);
        grid.Children.Add(button);
        Grid.SetRow(grid.Children[0], 0);
        Grid.SetRow(grid.Children[1], 1);

        currentWindow?.Close();
        currentWindow = new Window
        {
            Title = title,
            Content = grid,
            Width = 400,
            Height = 115,
            Padding = new Thickness(10),
        };

        void returnAction()
        {
            tcs.TrySetResult(textBox.Text);
            currentWindow?.Close();
        }
        textBox.KeyDown += (s, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                returnAction();
            }
        };
        button.Click += (s, e) =>
        {
            returnAction();
        };
        // Closing the window without pressing OK (window X, Escape, ...) counts as cancelling the input.
        currentWindow.Closed += (s, e) => tcs.TrySetResult("");

        currentWindow.ShowActivated = TakeFocus;
        // Focus the text box as soon as the dialog opens so the user can start typing right away.
        // (Focusing it after ShowDialog returns would be pointless - by then the window is closed.)
        currentWindow.Opened += (s, e) => textBox.Focus();
        await currentWindow.ShowDialog(OriginWindow!);

        return await tcs.Task;
    }

    private async Task<bool> ShowYesNoWindowAsync(string title, string message, bool TakeFocus = true)
    {
        var tcs = new TaskCompletionSource<bool>();

        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var yesButton = new Button
        {
            Content = "Yes",
            Width = 100,
            Height = 30
        };
        var noButton = new Button
        {
            Content = "No",
            Width = 100,
            Height = 30
        };
        var buttonStack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        buttonStack.Children.Add(yesButton);
        buttonStack.Children.Add(noButton);

        var grid = new Grid
        {
            Margin = new Thickness(10),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = new GridLength(40) },
            }
        };
        grid.Children.Add(message.Length > 1000 ? new ScrollViewer { Content = textBlock, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch } : textBlock);
        grid.Children.Add(buttonStack);
        Grid.SetRow(grid.Children[0], 0);
        Grid.SetRow(grid.Children[1], 1);

        currentWindow?.Close();
        currentWindow = new Window
        {
            Title = title,
            Content = grid,
            Width = 450,
            Height = 190,
            Padding = new Thickness(10),
        };

        void closeWithResult(bool result)
        {
            currentWindow?.Close();
            tcs.TrySetResult(result);
        }
        yesButton.Click += (s, e) => closeWithResult(true);
        noButton.Click += (s, e) => closeWithResult(false);
        // Closing the window without pressing a button counts as "No"
        currentWindow.Closed += (s, e) => tcs.TrySetResult(false);

        currentWindow.ShowActivated = TakeFocus;
        await currentWindow.ShowDialog(OriginWindow!);

        return await tcs.Task;
    }

    private void ShowPopupWindow(string title, string message, bool TakeFocus = true, double width = 400, double height = 115)
    {
        var window = BuildPopupWindow(title, message, TakeFocus, width, height);
        window.Show(OriginWindow!);
    }

    private async Task ShowPopupWindowAsync(string title, string message, bool TakeFocus = true, double width = 400, double height = 115)
    {
        var window = BuildPopupWindow(title, message, TakeFocus, width, height);
        await window.ShowDialog(OriginWindow!);
    }

    private Window BuildPopupWindow(string title, string message, bool TakeFocus, double width, double height)
    {
        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, // Centers text horizontally
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,     // Centers text vertically
            Width = 120,
            Height = 30
        };
        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var grid = new Grid
        {
            Margin = new Thickness(10),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = new GridLength(40) },
            }
        };
        grid.Children.Add(message.Length > 1000 ? new ScrollViewer { Content = textBlock, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch } : textBlock);
        grid.Children.Add(button);
        Grid.SetRow(grid.Children[0], 0);
        Grid.SetRow(grid.Children[1], 1);

        currentWindow?.Close();
        currentWindow = new Window
        {
            Title = title,
            //CanResize = false,
            Content = grid,
            Width = width,
            Height = height,
            Padding = new Thickness(10)
        };
        button.Click += (s, e) => currentWindow.Close();

        currentWindow.ShowActivated = TakeFocus;
        return currentWindow;
    }

    private void ShowPopupFlyout(string title, string message)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 18,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        var contentBlock = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        var grid = new Grid
        {
            Margin = new Thickness(10),
            RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Star },
                    new RowDefinition { Height = GridLength.Star },
                },
            RowSpacing = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        grid.Children.Add(titleBlock);
        grid.Children.Add(message.Length > 1000 ? new ScrollViewer { Content = contentBlock, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch } : contentBlock);
        Grid.SetRow(grid.Children[0], 0);
        Grid.SetRow(grid.Children[1], 1);

        currentFlyout?.Hide();
        currentFlyout = new Flyout
        {
            Content = grid,
            Placement = PlacementMode.Center,
            ShowMode = FlyoutShowMode.Transient,
        };
        Flyout.SetAttachedFlyout(FlyoutOrigin!, currentFlyout);
        currentFlyout.ShowAt(FlyoutOrigin!);
    }
}