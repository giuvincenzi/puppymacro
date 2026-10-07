using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>
/// Dedicated thread that owns the low-level hooks and runs their message loop. It never waits
/// for the user interface, so Windows never skips or removes the hooks because of a busy UI.
/// </summary>
internal sealed class InputThread : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Action<InputHook> _configure;
    private readonly Action _onDesktopSwitch;
    private Exception? _startError;
    private uint _threadId;
    private InputHook? _hook;
    private NativeMethods.WinEventProc? _desktopProc;
    private IntPtr _desktopHook;

    /// <param name="configure">Sets the hook handlers; runs on the input thread before the hooks are installed.</param>
    public InputThread(Action<InputHook> configure, Action onDesktopSwitch)
    {
        _configure = configure;
        _onDesktopSwitch = onDesktopSwitch;
        _thread = new Thread(Run) { IsBackground = true, Name = "PuppyMacro input", Priority = ThreadPriority.Highest };
    }

    /// <summary>The hook, once the thread has started.</summary>
    public InputHook? Hook => _hook;

    /// <summary>Starts the thread and waits until the hooks are installed (or failed).</summary>
    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        if (_startError != null)
            throw _startError;
    }

    private void Run()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = new InputHook();
            _configure(_hook);
            _hook.Install();

            // Key-up events are lost when Windows switches desktop (UAC prompt, Ctrl+Alt+Del, lock).
            _desktopProc = (_, _, _, _, _, _, _) => _onDesktopSwitch();
            _desktopHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH, NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH,
                IntPtr.Zero, _desktopProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        }
        catch (Exception ex)
        {
            _startError = ex is Win32Exception ? ex : new Win32Exception(ex.Message);
            _ready.Set();
            return;
        }
        _ready.Set();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        _hook.Dispose();
        if (_desktopHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_desktopHook);
    }

    public void Dispose()
    {
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}

/// <summary>
/// Sends input requested by the hook (remaps, menu mask key) from its own thread, in order,
/// so the hook callback itself only decides and returns.
/// </summary>
internal sealed class Injector : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public Injector()
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); } catch { /* never let one send stop the queue */ }
            }
        })
        { IsBackground = true, Name = "PuppyMacro injector", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Post(Action work)
    {
        if (!_queue.IsAddingCompleted)
            _queue.Add(work);
    }

    public void Dispose() => _queue.CompleteAdding();
}
