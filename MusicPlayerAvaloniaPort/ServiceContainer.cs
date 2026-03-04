using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Reflection;

namespace MusicPlayerAvaloniaPort;

[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
public class RegisterImplementation(ServiceRegisterType serviceRegisterType, Type serviceType) : Attribute
{
    public readonly ServiceRegisterType serviceRegisterType = serviceRegisterType;
    public readonly Type serviceType = serviceType;
}

public enum ServiceRegisterType { Singleton, Scoped, Transient }

public static class ServiceContainer
{
    static IServiceCollection serviceCollection;
    public static readonly IServiceProvider Services;

    static ServiceContainer()
    {
        serviceCollection = new ServiceCollection();

        // Local Assembly Services
        Type[] serviceTypes = (from domainAssembly in AppDomain.CurrentDomain.GetAssemblies()
                               from declaringType in domainAssembly.GetTypes()
                               where declaringType.Module == typeof(ServiceContainer).Module
                                   && declaringType.CustomAttributes.Any(x => x.AttributeType == typeof(RegisterImplementation))
                               select declaringType).ToArray();
        foreach (var declaringType in serviceTypes)
        {
            var attr = declaringType.GetCustomAttribute<RegisterImplementation>();

            if (attr == null || attr?.serviceType == null || attr?.serviceRegisterType == null) continue;

            if (attr.serviceRegisterType == ServiceRegisterType.Singleton)
                serviceCollection.AddSingleton(declaringType, attr.serviceType);
            else if (attr?.serviceRegisterType == ServiceRegisterType.Scoped)
                serviceCollection.AddScoped(declaringType, attr.serviceType);
            else if (attr?.serviceRegisterType == ServiceRegisterType.Transient)
                serviceCollection.AddTransient(declaringType, attr.serviceType);
        }

        Services = serviceCollection.BuildServiceProvider();
    }
}
