using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Services.UiUpdateLoop;

[AttributeUsage(AttributeTargets.Class)]
public class RegisterUiLoop() : Attribute { }

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(UiUpdateLoopService))]
public class UiUpdateLoopService
{
    readonly List<IUiUpdateLoopInput> Inputs = new();
    readonly List<IUiUpdateLoop> Loops = new();
    readonly List<IUiUpdateLoopEventHandler> EventHandlers = new();

    private ulong FrameCounter = 0;

    public UiUpdateLoopService()
    {
        // Local Assembly Services
        Type[] serviceTypes = (from domainAssembly in AppDomain.CurrentDomain.GetAssemblies()
                               from declaringType in domainAssembly.GetTypes()
                               where declaringType.Module == typeof(UiUpdateLoopService).Module
                                   && declaringType.CustomAttributes.Any(x => x.AttributeType == typeof(RegisterUiLoop))
                               select declaringType).ToArray();
        foreach (var declaringType in serviceTypes)
        {
            var attr = declaringType.GetCustomAttribute<RegisterUiLoop>();

            if (attr == null) continue;

            IUiUpdateLoop? loop = (IUiUpdateLoop?)Activator.CreateInstance(declaringType);
            if (loop == null) continue;
            Loops.Add(loop);
            EventHandlers.AddRange(loop.Events ?? []);
        }
    }

    public List<IUiUpdateLoop> GetLoopsForView(UserControl view)
    {
        return Loops.Where(loop => loop.BelongingView == view.GetType()).ToList();
    }

    public void AddInput(IUiUpdateLoopInput input)
    {
        var existingInput = Inputs.FirstOrDefault(x => x.GetType() == input.GetType());
        if (existingInput != null)
        {
            Inputs.Remove(existingInput);
        }
        Inputs.Add(input);
    }

    public void Init()
    {
        foreach (var loop in Loops)
        {
            var input = Inputs.FirstOrDefault(x => x.GetType() == loop.InputType);
            if (input != null)
            {
                loop.Init(input);
            }
        }
    }

    public void StartLoopThread()
    {
        DispatcherTimer.Run(() =>
        {
            foreach (var loop in Loops)
            {
                var input = Inputs.FirstOrDefault(x => x.GetType() == loop.InputType);
                if (input != null)
                {
                    loop.Update(input, FrameCounter);
                }
            }

            FrameCounter++;
            return true;
        }, TimeSpan.FromMilliseconds(900 / 60.0), DispatcherPriority.Render);
    }

    public void InvokeEvent<TEventArgs>(TEventArgs args)
    {
        foreach (var handler in EventHandlers)
        {
            if (handler is UiUpdateLoopEventHandler<TEventArgs> typedHandler)
            {
                typedHandler.Handle(args);
            }
        }
    }
}