using EzAuth.Interfaces;
using EzAuth.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using System;
using System.Net.Http;
using System.Runtime.CompilerServices;

namespace MusicPlayerAvaloniaPort;

/// <summary>
/// Wires the desktop client into the shared <see cref="ServiceContainer"/>: which assembly holds this
/// client's own <see cref="RegisterImplementation"/> services, which extra services only exist here
/// (the HTTP client and the auth backend), and what has to be initialised once the provider exists.
/// <para>
/// The shared services (database, sync, choosing, voting, volume, playback) come from
/// MusicPlayerClientCore and are registered by the container itself; only the desktop specific ones
/// (audio with FFT, key hook, downloads, MPRIS, system audio capture) are registered from here.
/// </para>
/// <para>
/// The <see cref="ModuleInitializerAttribute"/> guarantees this runs before any other code of this
/// assembly - including <c>Program.Main</c> - so the registrations are in place long before the first
/// service is resolved (which is what the container requires).
/// </para>
/// </summary>
public static class ServiceContainerSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Services of this assembly (audio backend, key hook, download processor, MPRIS, capture, diagram).
        ServiceContainer.AddServiceAssembly(typeof(ServiceContainerSetup).Assembly);

        ServiceContainer.AddServices(services =>
        {
            services.AddSingleton<HttpClient>(new HttpClient());
            services.AddSingleton<IEzAuth>(new EzKeycloak());
            if (OperatingSystem.IsLinux())
                services.AddSingleton<MprisService>();

            // The shared song services (playback, voting, volume normalization) depend on the audio
            // backend through IAudioPlaybackService, which only the application can bind - the desktop
            // client binds it to its SoundFlow backend with the FFT analysis (the mobile client binds it
            // to its own lean player). Resolving it to the very same singleton the views use keeps one
            // single audio pipeline (two would mean the volume normalization drives a silent one).
            services.AddSingleton<IAudioPlaybackService>(provider => provider.GetRequiredService<AudioLibWrapperService>());
        });

        ServiceContainer.AddStartupHook(_ =>
        {
            ServiceContainer.GetService<SongDownloadRequestProcessorService>().Init();
            if (!OperatingSystem.IsLinux())
                ServiceContainer.GetService<KeyHookService>().Init();
        });
    }
}
