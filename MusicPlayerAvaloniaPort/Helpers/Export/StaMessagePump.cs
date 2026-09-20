using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Helpers.Export;

/// <summary>
/// A thread of its own with an STA apartment and a real Win32 message pump, used for the shell work that
/// talks to a device connected over USB (see <see cref="WindowsShellFolder"/>).
///
/// The shell's copy engine needs its caller to be an STA thread that keeps dispatching messages, and the
/// shell objects involved (Shell.Application, the folder of the device) have to be created on that thread.
/// The app's UI thread does not do the job: copying from there is accepted by CopyHere and then never
/// happens - the folder the app holds never even learns about the new file. The same copy driven from a
/// thread like this one works, which is what the export uses now.
///
/// Work is queued and executed one item at a time; between items the loop dispatches the thread's messages,
/// so the shell can deliver its notifications. Callers never block: they await
/// <see cref="RunAsync{T}"/> and keep their own thread free.
/// </summary>
public sealed class StaMessagePump
{
    /// <summary>How long the loop sleeps when it has nothing to do (it must not spin).</summary>
    const int IdleSleepMilliseconds = 20;

    readonly ConcurrentQueue<Action> workItems = new();
    readonly Thread thread;
    volatile bool shutdown;

    public StaMessagePump()
    {
        thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ShellMessagePump"
        };

        // Only meaningful on Windows; the pump is only used for the Windows shell.
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
    }

    public bool IsPumpThread => Thread.CurrentThread == thread;

    /// <summary>Runs <paramref name="work"/> on the pump thread and completes when it returned.</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        if (IsPumpThread)
        {
            // Already there: run it inline so callers never deadlock on their own queue.
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        workItems.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    public Task RunAsync(Action work) => RunAsync(() =>
    {
        work();
        return true;
    });

    public void Dispose() => shutdown = true;

    void Loop()
    {
        while (!shutdown)
        {
            bool didWork = false;
            while (workItems.TryDequeue(out Action? work))
            {
                didWork = true;
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    // A work item may not take the pump down; its own caller already got the exception.
                    ExportLog.Write($"pump work item failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Dispatch everything that arrived for this apartment (the shell reports its copy progress and
            // completion this way, and a folder's view only refreshes while these are dispatched).
            while (PeekMessage(out NativeMessage message, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                didWork = true;
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (!didWork)
                Thread.Sleep(IdleSleepMilliseconds);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    const uint PM_REMOVE = 1;

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref NativeMessage message);
}
