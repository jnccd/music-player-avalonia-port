using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MusicPlayerAvaloniaPortMobile.ViewModels;
using MusicPlayerAvaloniaPortMobile.Views;

namespace MusicPlayerAvaloniaPortMobile;

/// <summary>
/// The Avalonia application of the mobile client. Android uses the single view lifetime (there are no
/// windows), so the whole app is one <see cref="MobileMainView"/>.
/// </summary>
public class MobileApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MobileMainView
            {
                DataContext = new MobileMainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
