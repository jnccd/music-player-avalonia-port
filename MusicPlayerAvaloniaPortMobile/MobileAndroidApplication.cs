using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace MusicPlayerAvaloniaPortMobile;

/// <summary>
/// Android application entry point. It exists (next to <see cref="MainActivity"/>) mainly to run the
/// platform bootstrap <b>before</b> anything else touches the client: the shared core resolves its config
/// and database paths lazily on first access, and on Android those have to point into the app's sandboxed
/// data directory instead of next to the (read only) executable.
/// </summary>
[Application(Label = "Music Player", Icon = "@mipmap/ic_launcher")]
public class MobileAndroidApplication : AvaloniaAndroidApplication<MobileApp>
{
    protected MobileAndroidApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        MobilePlatform.Initialize(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);
}
