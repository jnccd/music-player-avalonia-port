using System;

namespace MusicPlayerAvaloniaPort.Services;

public class RecreatableService<T>(T Instance) where T : class
{
    public T Instance { get; set; } = Instance;
}