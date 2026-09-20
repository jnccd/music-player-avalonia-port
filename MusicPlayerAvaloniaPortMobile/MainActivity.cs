using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using System;

namespace MusicPlayerAvaloniaPortMobile;

/// <summary>
/// The single activity of the mobile client (Avalonia renders the whole app into it).
/// </summary>
[Activity(
    Label = "Music Player",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity
{
    const int AudioPermissionRequestCode = 4711;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The platform bootstrap normally already ran (see MobileAndroidApplication.OnCreate); calling it
        // again is a no-op and keeps the activity working if Android ever recreates it in a fresh process.
        MobilePlatform.Initialize(this);

        // Resize instead of panning: the settings sheet is a scrollable panel, so the layout can follow the
        // keyboard (unlike the notes app, whose editor rows needed a stable viewport).
        Window?.SetSoftInputMode(SoftInput.AdjustResize);

        RequestAudioPermission();
    }

    /// <summary>
    /// Asks for the media read permission the library scan needs. Android 13+ uses READ_MEDIA_AUDIO, older
    /// versions the blanket READ_EXTERNAL_STORAGE. The user can deny it - the app then simply finds no
    /// songs and the main view says so, instead of crashing.
    /// </summary>
    void RequestAudioPermission()
    {
        // OperatingSystem.IsAndroidVersionAtLeast (instead of Build.VERSION.SdkInt) is what the platform
        // compatibility analyzer understands, so the API 33 permission is only touched on API 33+.
        string permission = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? Manifest.Permission.ReadMediaAudio!
            : Manifest.Permission.ReadExternalStorage!;

        if (CheckSelfPermission(permission) == Permission.Granted)
            return;

        RequestPermissions([permission], AudioPermissionRequestCode);
    }
}
