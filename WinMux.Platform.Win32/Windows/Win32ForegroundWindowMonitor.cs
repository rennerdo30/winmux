using System.Runtime.InteropServices;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Windows' own notification that the active window changed.
///
/// <c>SetWinEventHook</c> with <c>EVENT_SYSTEM_FOREGROUND</c> rather than polling the foreground
/// window on a timer: polling either misses a click that focuses and unfocuses between ticks, or
/// runs often enough to be a cost the shell pays forever for something that happens a few times a
/// minute.
///
/// Registered <c>WINEVENT_OUTOFCONTEXT</c>, so the callback is delivered to this process on the
/// thread that installed the hook, through its message queue — no DLL is injected into the
/// applications being watched, which is why this needs no elevation and cannot destabilise them.
/// The consequence is that the installing thread must pump messages, which the shell's UI thread
/// does.
///
/// Nothing here calls into the foreign window. <c>GetAncestor</c> and
/// <c>GetWindowThreadProcessId</c> read the window manager's own bookkeeping and do not wait on the
/// thread that owns the window, unlike the calls ADR 0001 measured freezing the shell for six
/// seconds.
/// </summary>
public sealed class Win32ForegroundWindowMonitor : IForegroundWindowMonitor
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint GaRoot = 2;

    public event Action<ForegroundWindow>? Changed;

    public IDisposable Start()
    {
        // The delegate must outlive the hook: if it is collected, the next foreground change calls
        // into freed memory and takes the process with it.
        var callback = new NativeMethods.WinEventProc(OnWinEvent);

        var hook = NativeMethods.SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            callback,
            processId: 0,
            threadId: 0,
            // Our own windows are not interesting: Avalonia already tells the shell about focus
            // inside its own controls, and reacting to both would fight.
            WineventOutOfContext | WineventSkipOwnProcess);

        return new Subscription(hook, callback);
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint thread,
        uint time)
    {
        // OBJID_WINDOW only. A foreground change also reports the caret and the client area, and
        // acting on those would fire three times for one click.
        const int ObjIdWindow = 0;
        if (eventType != EventSystemForeground || objectId != ObjIdWindow || window == nint.Zero) return;
        if (!NativeMethods.IsWindow(window)) return;

        var root = NativeMethods.GetAncestor(window, GaRoot);
        if (root == nint.Zero) root = window;

        NativeMethods.GetWindowThreadProcessId(root, out var processId);

        Changed?.Invoke(new ForegroundWindow(
            WindowHandle.FromPlatformValue(window),
            WindowHandle.FromPlatformValue(root),
            processId));
    }

    private static class NativeMethods
    {
        private const string User32 = "user32.dll";

        internal delegate void WinEventProc(
            nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

        [DllImport(User32)]
        internal static extern nint SetWinEventHook(
            uint eventMin,
            uint eventMax,
            nint module,
            WinEventProc callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport(User32)] internal static extern bool UnhookWinEvent(nint hook);
        [DllImport(User32)] internal static extern bool IsWindow(nint hwnd);
        [DllImport(User32)] internal static extern nint GetAncestor(nint hwnd, uint flags);
        [DllImport(User32)] internal static extern uint GetWindowThreadProcessId(nint window, out int processId);
    }

    private sealed class Subscription(nint hook, NativeMethods.WinEventProc callback) : IDisposable
    {
        // Held only to keep the delegate alive for as long as the hook is installed.
        private readonly NativeMethods.WinEventProc _callback = callback;
        private nint _hook = hook;

        public void Dispose()
        {
            if (_hook == nint.Zero) return;
            NativeMethods.UnhookWinEvent(_hook);
            _hook = nint.Zero;
            GC.KeepAlive(_callback);
        }
    }
}
