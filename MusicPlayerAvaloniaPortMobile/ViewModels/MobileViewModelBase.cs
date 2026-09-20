using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MusicPlayerAvaloniaPortMobile.ViewModels;

/// <summary>
/// Minimal observable base for the mobile view model. The desktop client uses CommunityToolkit.Mvvm, but
/// this single view does not need source generators or commands - a plainly written <c>SetProperty</c> is
/// easier to follow here and keeps the mobile head free of an extra dependency.
/// </summary>
public abstract class MobileViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
