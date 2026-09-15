using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputQueueSpike;

/// <summary>
/// The window standing in for WinMux's shell: it records when a keystroke actually arrives.
///
/// Its own thread runs the message loop, because the thing being measured is whether *this thread's*
/// input queue keeps delivering while another thread's owner is wedged.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Lock _gate = new();

    private long _sentAt;
    private int _received;
    private double _worstMs;

    public nint Handle { get; private set; }

    public int Received { get { lock (_gate) return _received; } }

    public double WorstLatencyMs { get { lock (_gate) return _worstMs; } }

    public HostWindow()
    {
        _thread = new Thread(Run) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _received = 0;
            _worstMs = 0;
        }
    }

    /// <summary>Record when a key was synthesized, so its arrival can be timed.</summary>
    public void ExpectAt(long timestamp)
    {
        lock (_gate) _sentAt = timestamp;
    }

    /// <summary>
    /// Take the foreground, and say whether it worked.
    ///
    /// Two traps, both of which silently produced a harness that measured nothing:
    /// <c>SetFocus</c> only works from the thread that owns the window, and Windows refuses
    /// <c>SetForegroundWindow</c> from a process that is not already in the foreground. The
    /// documented way round the second is to borrow the current foreground thread's input queue
    /// for the duration — which is, pointedly, the same mechanism this spike is here to measure,
    /// used deliberately and released immediately.
    /// </summary>
    public bool Activate()
    {
        Native.ShowWindow(Handle, Native.SwShow);

        var foreground = Native.GetForegroundWindow();
        var theirThread = Native.GetWindowThreadProcessId(foreground, out _);
        var ourThread = Native.GetCurrentThreadId();

        var borrowed = theirThread != 0 && theirThread != ourThread &&
                       Native.AttachThreadInput(ourThread, theirThread, true);
        try
        {
            Native.SetForegroundWindow(Handle);
            Native.BringWindowToTop(Handle);

            // SetFocus has to run on the owning thread, so ask that thread to do it.
            Native.SendMessage(Handle, Native.WmTakeFocus, nint.Zero, nint.Zero);
        }
        finally
        {
            if (borrowed) Native.AttachThreadInput(ourThread, theirThread, false);
        }

        Thread.Sleep(200);
        return Native.GetForegroundWindow() == Handle;
    }

    private void Run()
    {
        Handle = Native.CreateSimpleWindow("WinMuxInputSpikeHost", "input spike host", 40, 40, 640, 400, WndProc);
        _ready.Set();

        while (Native.GetMessage(out var message, nint.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    private nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == Native.WmTakeFocus)
        {
            Native.SetFocus(window);
            return 0;
        }

        if (message == Native.WmKeyDown)
        {
            lock (_gate)
            {
                _received++;
                if (_sentAt != 0)
                {
                    var ms = Stopwatch.GetElapsedTime(_sentAt).TotalMilliseconds;
                    if (ms > _worstMs) _worstMs = ms;
                }
            }
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Native.IsWindow(Handle)) Native.PostMessage(Handle, Native.WmClose, nint.Zero, nint.Zero);
        _ready.Dispose();
    }
}

/// <summary>
/// The application being hosted. Pumps normally until told to wedge, then stops answering —
/// the same failure spike 3 used, because it is what a real frozen application does.
/// </summary>
internal sealed class AppWindow : IDisposable
{
    private const int WedgeSeconds = 6;

    public nint Handle { get; }

    public AppWindow() =>
        Handle = Native.CreateSimpleWindow("WinMuxInputSpikeApp", "input spike app", 700, 40, 480, 320, WndProc);

    public void Pump()
    {
        while (Native.GetMessage(out var message, nint.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    private nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == Native.WmWedge)
        {
            // Not answering. This is the whole experiment.
            Thread.Sleep(TimeSpan.FromSeconds(WedgeSeconds));
            return 0;
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Native.IsWindow(Handle)) Native.DestroyWindow(Handle);
    }
}

internal static class Native
{
    private const string User32 = "user32.dll";

    internal const uint WmClose = 0x0010;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmWedge = 0x0400 + 1; // WM_APP + 1
    internal const uint WmTakeFocus = 0x0400 + 2;
    internal const int SwShow = 5;

    internal delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    // Held for the life of the process: a collected delegate means a wild jump on the next message.
    private static readonly List<WndProc> Kept = [];

    internal static nint CreateSimpleWindow(
        string className, string title, int x, int y, int width, int height, WndProc proc)
    {
        Kept.Add(proc);

        var wc = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
            hCursor = LoadCursor(nint.Zero, 32512), // IDC_ARROW
        };

        RegisterClass(ref wc);

        const uint WsOverlappedWindow = 0x00CF0000;
        var window = CreateWindowEx(
            0, className, title, WsOverlappedWindow, x, y, width, height,
            nint.Zero, nint.Zero, wc.hInstance, nint.Zero);

        if (window == nint.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        ShowWindow(window, SwShow);
        return window;
    }

    /// <summary>One synthesized keystroke, delivered wherever the focus actually is.</summary>
    internal static void SendKey()
    {
        const ushort VkF13 = 0x7C; // Nothing else uses it, so a stray one cannot do damage.
        Input[] input =
        [
            new() { type = 1, u = new InputUnion { ki = new KeyboardInput { wVk = VkF13 } } },
            new() { type = 1, u = new InputUnion { ki = new KeyboardInput { wVk = VkF13, dwFlags = 2 } } },
        ];

        SendInput((uint)input.Length, input, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput ki;
        [FieldOffset(0)] private readonly MouseFiller mouse;
    }

    /// <summary>Only here to make the union the size the mouse variant needs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseFiller
    {
        private readonly int dx, dy;
        private readonly uint mouseData, dwFlags, time;
        private readonly nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass wndClass);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport(User32)] private static extern nint LoadCursor(nint instance, int cursor);
    [DllImport(User32)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int GetMessage(out Msg message, nint window, uint filterMin, uint filterMax);

    [DllImport(User32)] internal static extern bool TranslateMessage(ref Msg message);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern nint DispatchMessage(ref Msg message);

    [DllImport(User32, SetLastError = true)]
    internal static extern nint SetParent(nint child, nint parent);

    [DllImport(User32)] internal static extern bool IsWindow(nint window);
    [DllImport(User32)] internal static extern bool DestroyWindow(nint window);
    [DllImport(User32)] internal static extern bool ShowWindow(nint window, int command);
    [DllImport(User32)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport(User32)] internal static extern nint SetFocus(nint window);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport(User32)] internal static extern nint GetForegroundWindow();
    [DllImport(User32)] internal static extern bool BringWindowToTop(nint window);
    [DllImport(User32)] internal static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport(User32)] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
}
