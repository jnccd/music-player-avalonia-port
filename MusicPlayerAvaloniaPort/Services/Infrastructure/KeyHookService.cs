using System;
using MusicPlayerAvaloniaPort.Services.Song;
using SharpHook.Reactive;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(KeyHookService))]
public class KeyHookService(AudioLibWrapperService audioLibWrapperService, SongPlaybackService songPlaybackService) : IDisposable
{
    private readonly ReactiveGlobalHook _hook = new ReactiveGlobalHook();
    private IDisposable? _subscription;

    public void Init()
    {
        if (_subscription == null)
        {
            _subscription = _hook.KeyPressed.Subscribe(e =>
            {
                if (e.Data.KeyCode == SharpHook.Data.KeyCode.VcMediaPlay)
                {
                    audioLibWrapperService.TogglePlayPause();
                }

                if (e.Data.KeyCode == SharpHook.Data.KeyCode.VcMediaNext)
                {
                    songPlaybackService.GetNextSong();
                }

                if (e.Data.KeyCode == SharpHook.Data.KeyCode.VcMediaPrevious)
                {
                    songPlaybackService.GetPreviousSong();
                }

                if (e.Data.KeyCode == SharpHook.Data.KeyCode.VcMediaStop)
                {
                    songPlaybackService.UpvoteLockedIn = !songPlaybackService.UpvoteLockedIn;
                }
            });
            _hook.RunAsync();
        }
    }

    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        _hook.Dispose();
    }

    public void Dispose() => Stop();
}