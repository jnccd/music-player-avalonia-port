using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MusicPlayerAvaloniaPort;

/// <summary>
/// Marks a class as a service that the <see cref="ServiceContainer"/> registers automatically. The
/// attribute is declared in the shared core assembly so that both the desktop and the mobile client can
/// decorate their own platform specific services with it.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
public class RegisterImplementation(ServiceRegisterType serviceRegisterType, Type serviceType) : Attribute
{
    public readonly ServiceRegisterType serviceRegisterType = serviceRegisterType;
    public readonly Type serviceType = serviceType;
}

public enum ServiceRegisterType { Singleton, Scoped, Transient }

/// <summary>
/// The tiny service container both clients resolve their services from.
/// <para>
/// It used to scan only the assembly it lived in. Now that the services are split over the shared core
/// assembly and the platform specific application assembly, it scans the core assembly plus every
/// assembly an application registers with <see cref="AddServiceAssembly"/> - the applications do that
/// from a <c>[ModuleInitializer]</c> (see <c>MusicPlayerAvaloniaPort/ServiceContainerSetup.cs</c> and
/// <c>MusicPlayerAvaloniaPortMobile/ServiceContainerSetup.cs</c>), which is guaranteed to run before any
/// other code of that application and therefore before the first <see cref="GetService{TRequested}"/>.
/// </para>
/// <para>
/// The provider is built lazily on first use, and <see cref="AddServices"/> /
/// <see cref="AddStartupHook"/> may only be called before that happens (they throw afterwards), so a
/// forgotten registration is a loud startup error instead of a service that silently stays missing.
/// </para>
/// </summary>
public static class ServiceContainer
{
    static readonly IServiceCollection serviceCollection = new ServiceCollection();
    static readonly List<Assembly> serviceAssemblies = [];
    static readonly List<Action<IServiceCollection>> serviceRegistrations = [];
    static readonly List<Action<IServiceProvider>> startupHooks = [];
    static readonly object buildLock = new();

    static IServiceProvider? services;

    /// <summary>
    /// Adds an application assembly whose <see cref="RegisterImplementation"/> types should be registered
    /// next to the shared core services. Has to be called before the container is built (first resolve).
    /// </summary>
    public static void AddServiceAssembly(Assembly assembly)
    {
        lock (buildLock)
        {
            ThrowIfBuilt();
            if (!serviceAssemblies.Contains(assembly))
                serviceAssemblies.Add(assembly);
        }
    }

    /// <summary>
    /// Registers application specific services that are not expressed with <see cref="RegisterImplementation"/>
    /// (the HTTP client, the auth backend, ...). Has to be called before the container is built.
    /// </summary>
    public static void AddServices(Action<IServiceCollection> configure) => AddRegistration(configure);

    /// <summary>
    /// Runs right after the provider was built, e.g. to kick off services that need an explicit Init()
    /// once their dependencies exist.
    /// </summary>
    public static void AddStartupHook(Action<IServiceProvider> hook)
    {
        lock (buildLock)
        {
            ThrowIfBuilt();
            startupHooks.Add(hook);
        }
    }

    static void AddRegistration(Action<IServiceCollection> configure)
    {
        lock (buildLock)
        {
            ThrowIfBuilt();
            serviceRegistrations.Add(configure);
        }
    }

    static void ThrowIfBuilt()
    {
        if (services != null)
            throw new InvalidOperationException(
                $"The {nameof(ServiceContainer)} was already built - add assemblies/services before the first resolve.");
    }

    static IServiceProvider Services
    {
        get
        {
            var built = services;
            if (built != null)
                return built;

            lock (buildLock)
            {
                if (services == null)
                    services = Build();
                return services;
            }
        }
    }

    static IServiceProvider Build()
    {
        var assembliesToScan = new List<Assembly> { typeof(ServiceContainer).Assembly };
        assembliesToScan.AddRange(serviceAssemblies);

        foreach (var assembly in assembliesToScan.Distinct())
        {
            foreach (var declaringType in GetLoadableTypes(assembly))
            {
                var attr = declaringType.GetCustomAttribute<RegisterImplementation>();
                if (attr?.serviceType == null)
                    continue;

                switch (attr.serviceRegisterType)
                {
                    // NOTE the argument order: AddSingleton/AddScoped/AddTransient(Type serviceType,
                    // Type implementationType). The inherited code passed (declaringType, attr.serviceType),
                    // which only ever worked because every service in this solution names itself in the
                    // attribute (serviceType == the declaring class). An attribute that names an interface
                    // would have registered the interface as the *implementation* of the class and blown up
                    // at resolve time.
                    case ServiceRegisterType.Singleton:
                        serviceCollection.AddSingleton(attr.serviceType, declaringType);
                        break;
                    case ServiceRegisterType.Scoped:
                        serviceCollection.AddScoped(attr.serviceType, declaringType);
                        break;
                    case ServiceRegisterType.Transient:
                        serviceCollection.AddTransient(attr.serviceType, declaringType);
                        break;
                }
            }
        }

        foreach (var register in serviceRegistrations)
            register(serviceCollection);

        var provider = serviceCollection.BuildServiceProvider();

        // Published before the startup hooks run: a hook resolves services itself (that is what it is for),
        // and without this it would find the container unbuilt and re-enter Build() forever.
        services = provider;

        foreach (var hook in startupHooks)
            hook(provider);

        return provider;
    }

    /// <summary>
    /// Types of an assembly, tolerating the ones that cannot be loaded (a partially trimmed or otherwise
    /// incomplete assembly should not take the whole container down).
    /// </summary>
    static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }

    public static TRequested GetService<TRequested>()
    {
        var service = Services.GetService<TRequested>()
            ?? throw new Exception($"Service of type {typeof(TRequested)} not found. Make sure it is decorated with [RegisterImplementation] and that its dependencies can be resolved.");
        return service;
    }

    public static TRequested? TryGetService<TRequested>() => Services.GetService<TRequested>();
}
