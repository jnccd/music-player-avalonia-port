using EzAuth.Interfaces;
using EzAuth.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayerAvaloniaPort;
using MusicPlayerAvaloniaPort.Services.Infrastructure;
using MusicPlayerAvaloniaPortMobile.Services;
using MusicPlayerAvaloniaPortMobile.ViewModels;
using System;
using System.Net.Http;
using System.Runtime.CompilerServices;

namespace MusicPlayerAvaloniaPortMobile;

/// <summary>
/// Wires the mobile client into the shared <see cref="ServiceContainer"/>. The shared services (database,
/// sync, choosing, voting, volume, playback orchestration) are registered by the container itself from
/// MusicPlayerClientCore; this adds the assembly holding the mobile specific services and binds the audio
/// abstraction to the mobile player.
/// <para>
/// The desktop client has the mirror image of this file - the two are the only places that know how a
/// platform satisfies the shared core.
/// </para>
/// </summary>
public static class ServiceContainerSetup
{
    // CA2255 is a warning about ModuleInitializer in libraries; this IS the application assembly, and the
    // initializer is exactly the right tool here: the container must know its assemblies before the very
    // first resolve, which happens while the Android runtime creates the activity.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        ServiceContainer.AddServiceAssembly(typeof(ServiceContainerSetup).Assembly);

        ServiceContainer.AddServices(services =>
        {
            // A phone spends a lot of time on mobile networks: a hung request must not keep a queued sync
            // request (or a login) blocked forever.
            services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
            services.AddSingleton<IEzAuth>(new EzKeycloak());

            // The shared song services depend on the audio backend only through IAudioPlaybackService; the
            // lean mobile player satisfies it. Resolving it to the same singleton the UI binds to keeps a
            // single audio pipeline, so volume normalization and the play/pause button act on the same one.
            services.AddSingleton<IAudioPlaybackService>(provider => provider.GetRequiredService<MobileAudioPlayerService>());

            services.AddSingleton<MobileMainViewModel>();
        });
    }
#pragma warning restore CA2255
}
