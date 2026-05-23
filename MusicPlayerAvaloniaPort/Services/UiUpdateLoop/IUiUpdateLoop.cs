using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace MusicPlayerAvaloniaPort.Services.UiUpdateLoop;

public interface IUiUpdateLoopInput { }
public interface IUiUpdateLoopEventHandler { }
public record UiUpdateLoopEventHandler<TEventArgs>(Action<TEventArgs, IUiUpdateLoopInput> Handle) : IUiUpdateLoopEventHandler;

public abstract class IUiUpdateLoop(Type BelongingView, Type InputType)
{
    public Type BelongingView { get; init; } = BelongingView;
    public Type InputType { get; init; } = InputType;
    public virtual List<IUiUpdateLoopEventHandler>? Events { get; }

    public abstract void Init(IUiUpdateLoopInput input);
    public abstract void Update(IUiUpdateLoopInput input, ulong frameCounter);
}