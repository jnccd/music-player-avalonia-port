using System;
using Avalonia.Media;
using MusicPlayerAvaloniaPort.Persistence.Configuration;

namespace MusicPlayerAvaloniaPort.Helpers;

/// <summary>
/// The app-wide primary ("accent") color - the Avalonia port's counterpart to the DxMGP client's
/// Program.game.primaryColor/secondaryColor pair. <see cref="ConfigData.PrimaryColor"/> is the persisted
/// value, the brushes here are what the views actually paint with, and <see cref="PrimaryColorChanged"/>
/// tells the hand-rendered parts of the main window (diagram, progress bar) that they have to pick up the
/// new brushes.
///
/// The brushes are REPLACED on every change instead of being mutated in place: a control that binds to a
/// brush (or that had the brush assigned to one of its properties) only repaints when it sees a different
/// instance - <see cref="MusicPlayerAvaloniaPort.ViewModels.MainViewModel"/> therefore exposes these
/// brushes as bindable properties instead of the main view using static XAML resources.
/// </summary>
public static class ThemeColors
{
    public const string DefaultPrimaryColorHex = "#007B82";

    /// <summary>
    /// How far the brighter shade of the primary color is mixed towards white. 0.4 is what the DxMGP
    /// client uses (Color.Lerp(primaryColor, White, 0.4f)), so both clients derive the same second shade -
    /// the default #007B82 brightens to #66AFB4.
    /// </summary>
    const double BrighterMix = 0.4;

    /// <summary>
    /// Raised on the UI thread after the primary color changed and the brushes were replaced, so controls
    /// that keep a reference to them can re-read and invalidate themselves.
    /// </summary>
    public static event Action? PrimaryColorChanged;

    public static Color DefaultPrimaryColor => Color.Parse(DefaultPrimaryColorHex);

    public static Color PrimaryColor { get; private set; } = LoadPrimaryColor();
    public static SolidColorBrush PrimaryBrush { get; private set; } = new(PrimaryColor);
    public static SolidColorBrush PrimaryBrighterBrush { get; private set; } = new(Brighter(PrimaryColor));

    /// <summary>
    /// Applies <paramref name="color"/> as the primary color and repaints everything that uses it.
    /// Pass <paramref name="save"/> = false while a picker drag is still running; the caller then persists
    /// the config once when the interaction finished.
    /// </summary>
    public static void SetPrimaryColor(Color color, bool save = true)
    {
        color = Opaque(color);
        PrimaryColor = color;
        PrimaryBrush = new SolidColorBrush(color);
        PrimaryBrighterBrush = new SolidColorBrush(Brighter(color));
        Config.Data.PrimaryColor = color;

        if (save)
            Config.Save();

        PrimaryColorChanged?.Invoke();
    }

    public static void ResetToDefault() => SetPrimaryColor(DefaultPrimaryColor);

    /// <summary>
    /// The second shade the DxMGP client derives with Color.Lerp(primaryColor, White, 0.4f): every channel
    /// (alpha included) is moved 40% towards white, truncating like XNA's byte cast did.
    /// </summary>
    public static Color Brighter(Color color) => Color.FromArgb(
        LerpTowardsWhite(color.A),
        LerpTowardsWhite(color.R),
        LerpTowardsWhite(color.G),
        LerpTowardsWhite(color.B));

    /// <summary>
    /// Drops the alpha channel: the primary color is always opaque because a transparent accent (or the
    /// transparent black that old config files hold, see ColorJsonConverter) would be invisible.
    /// </summary>
    public static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);

    /// <summary>The "#RRGGBB" form shown in (and accepted by) the options window.</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    static byte LerpTowardsWhite(byte channel) => (byte)(channel + (255 - channel) * BrighterMix);

    static Color LoadPrimaryColor()
    {
        var color = Config.Data.PrimaryColor;

        // Alpha 0 is "never set": it is what every config written before the color was configurable holds.
        if (color.A == 0)
            color = DefaultPrimaryColor;

        // Remember the color that is actually in use, so the next save (the window position, the volume,
        // a picked color, ...) stores a usable value instead of the legacy transparent one.
        color = Opaque(color);
        Config.Data.PrimaryColor = color;
        return color;
    }
}
