using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinMux.PaneHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--detach-host", var processText] && uint.TryParse(processText, out var processId))
                return HostControl.Detach(processId) ? 0 : 1;

            using var host = new PaneHostWindow(LaunchSpec.Parse(args));
            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WinMux.PaneHost: {ex.Message}");
            return 1;
        }
    }
}

internal static class HostControl
{
    public static bool Detach(uint processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != processId) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found != IntPtr.Zero && PostMessageW(found, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

internal sealed record LaunchSpec(
    string Program,
    IReadOnlyList<string> Arguments,
    string? WindowClass,
    IntPtr OwnerWindow,
    IReadOnlySet<IntPtr> ExcludedWindows)
{
    public static LaunchSpec Parse(string[] args)
    {
        string? program = null;
        string? windowClass = null;
        var ownerWindow = IntPtr.Zero;
        var arguments = new List<string>();
        var excluded = new HashSet<IntPtr>();
        var afterSeparator = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (!afterSeparator && args[i] == "--") { afterSeparator = true; continue; }
            if (!afterSeparator && args[i] == "--program" && ++i < args.Length) { program = args[i]; continue; }
            if (!afterSeparator && args[i] == "--window-class" && ++i < args.Length) { windowClass = args[i]; continue; }
            if (!afterSeparator && args[i] == "--owner" && ++i < args.Length && long.TryParse(args[i], out var owner))
            {
                ownerWindow = new IntPtr(owner);
                continue;
            }
            if (!afterSeparator && args[i] == "--exclude" && ++i < args.Length && long.TryParse(args[i], out var handle))
            {
                excluded.Add(new IntPtr(handle));
                continue;
            }
            if (afterSeparator) arguments.Add(args[i]);
        }

        if (string.IsNullOrWhiteSpace(program)) throw new ArgumentException("missing --program <path>");
        return new LaunchSpec(program, arguments, windowClass, ownerWindow, excluded);
    }
}

internal sealed class PaneHostWindow : IDisposable
{
    private const string WindowClassName = "WinMux.PaneHost.Window";
    private const int GwlStyle = -16, GwlExStyle = -20, SwShowNormal = 1, SwRestore = 9;
    private const int WsPopup = unchecked((int)0x80000000), WsVisible = 0x10000000;
    private const long ForeignFrameStyles = 0x00CF0000L;
    private const uint GaParent = 1, GwOwner = 4, WmClose = 0x0010, WmDestroy = 0x0002, WmSize = 0x0005;
    private const uint WmAppAdopt = 0x8001, WmAppClosePane = 0x8002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpFrameChanged = 0x0020;

    private readonly LaunchSpec _launch;
    private readonly WindowProc _windowProc;
    private readonly CancellationTokenSource _shutdown = new();
    private IntPtr _hostWindow;
    private IntPtr _childWindow;
    private EmbedState? _embed;
    private bool _disposed;

    public PaneHostWindow(LaunchSpec launch) { _launch = launch; _windowProc = WindowProcImpl; }

    public void Run()
    {
        RegisterWindowClass();
        _hostWindow = CreateWindowExW(0, WindowClassName, "WinMux pane host", WsPopup | WsVisible,
            100, 100, 900, 600, _launch.OwnerWindow, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hostWindow == IntPtr.Zero) ThrowLastError("CreateWindowEx");

        ShowWindow(_hostWindow, SwShowNormal);
        WriteProtocol($"HOST_HWND={_hostWindow.ToInt64()}");
        _ = Task.Run(FindApplicationWindow);
        _ = Task.Run(ReadCommands);

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private void RegisterWindowClass()
    {
        var windowClass = new WindowClass
        {
            WindowProc = _windowProc,
            Instance = GetModuleHandleW(null),
            Cursor = LoadCursorW(IntPtr.Zero, 32512),
            ClassName = WindowClassName,
        };
        if (RegisterClassW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
            ThrowLastError("RegisterClass");
    }

    private void FindApplicationWindow()
    {
        try
        {
            var before = AdoptableWindows().ToHashSet();
            var start = new ProcessStartInfo(_launch.Program) { UseShellExecute = false };
            foreach (var argument in _launch.Arguments) start.ArgumentList.Add(argument);
            _ = Process.Start(start) ?? throw new InvalidOperationException("could not start foreign application");

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(8) && !_shutdown.IsCancellationRequested)
            {
                var candidate = AdoptableWindows().LastOrDefault(hwnd =>
                    !before.Contains(hwnd) && !_launch.ExcludedWindows.Contains(hwnd) && Matches(hwnd));
                if (candidate != IntPtr.Zero) { PostMessageW(_hostWindow, WmAppAdopt, candidate, IntPtr.Zero); return; }
                Thread.Sleep(100);
            }

            var existing = AdoptableWindows().LastOrDefault(hwnd =>
                !_launch.ExcludedWindows.Contains(hwnd) && Matches(hwnd));
            if (existing != IntPtr.Zero) { PostMessageW(_hostWindow, WmAppAdopt, existing, IntPtr.Zero); return; }
            if (!_shutdown.IsCancellationRequested) WriteProtocol("ERROR=no matching window appeared within 8 seconds");
        }
        catch (Exception ex)
        {
            WriteProtocol("ERROR=" + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
        }
    }

    private async Task ReadCommands()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested && await Console.In.ReadLineAsync(_shutdown.Token) is { } command)
            {
                switch (command.Trim().ToUpperInvariant())
                {
                    case "DETACH":
                        PostMessageW(_hostWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
                        return;
                    case "CLOSE":
                        PostMessageW(_hostWindow, WmAppClosePane, IntPtr.Zero, IntPtr.Zero);
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool Matches(IntPtr hwnd) => !string.IsNullOrWhiteSpace(_launch.WindowClass)
        ? GetClassName(hwnd).Equals(_launch.WindowClass, StringComparison.OrdinalIgnoreCase)
        : GetProcessImageName(hwnd).Equals(Path.GetFileName(_launch.Program), StringComparison.OrdinalIgnoreCase);

    private void Embed(IntPtr child)
    {
        if (!IsWindow(child)) return;
        if (!GetWindowRect(child, out var originalRect)) ThrowLastError("GetWindowRect");
        _embed = new EmbedState(child, GetAncestor(child, GaParent), GetWindowLongPtrW(child, GwlStyle),
            GetWindowLongPtrW(child, GwlExStyle), originalRect);

        if (IsIconic(child)) ShowWindow(child, SwRestore);
        SetWindowLongPtrW(child, GwlStyle, new IntPtr(_embed.OriginalStyle.ToInt64() & ~ForeignFrameStyles));
        Marshal.SetLastPInvokeError(0);
        var oldParent = SetParent(child, _hostWindow);
        var error = Marshal.GetLastPInvokeError();
        if (oldParent == IntPtr.Zero && error != 0)
        {
            RestoreForeignWindow();
            WriteProtocol(error switch
            {
                5 => "ERROR=application is elevated; WinMux cannot embed it",
                87 => "ERROR=application refused embedding; attach mode is required",
                _ => $"ERROR=SetParent failed with Win32 error {error}",
            });
            return;
        }

        _childWindow = child;
        ResizeChild();
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        WriteProtocol($"READY={child.ToInt64()}");
    }

    private void ResizeChild()
    {
        if (_childWindow == IntPtr.Zero || !IsWindow(_childWindow) || !GetClientRect(_hostWindow, out var rect)) return;
        SetWindowPos(_childWindow, IntPtr.Zero, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    private void RestoreForeignWindow()
    {
        if (_embed is not { } embed || !IsWindow(embed.Child)) return;
        SetParent(embed.Child, embed.OriginalParent);
        SetWindowLongPtrW(embed.Child, GwlStyle, embed.OriginalStyle);
        SetWindowLongPtrW(embed.Child, GwlExStyle, embed.OriginalExStyle);
        SetWindowPos(embed.Child, IntPtr.Zero, embed.OriginalRect.Left, embed.OriginalRect.Top,
            embed.OriginalRect.Right - embed.OriginalRect.Left, embed.OriginalRect.Bottom - embed.OriginalRect.Top,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        _embed = null;
        _childWindow = IntPtr.Zero;
    }

    private IntPtr WindowProcImpl(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmAppAdopt:
                try { Embed(wParam); } catch (Exception ex) { WriteProtocol("ERROR=" + ex.Message); }
                return IntPtr.Zero;
            case WmSize: ResizeChild(); return IntPtr.Zero;
            case WmAppClosePane:
                var child = _childWindow;
                RestoreForeignWindow();
                if (child != IntPtr.Zero && IsWindow(child)) PostMessageW(child, WmClose, IntPtr.Zero, IntPtr.Zero);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmClose:
                RestoreForeignWindow();
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy: PostQuitMessage(0); return IntPtr.Zero;
            default: return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private static void WriteProtocol(string line) { Console.Out.WriteLine(line); Console.Out.Flush(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        RestoreForeignWindow();
        if (_hostWindow != IntPtr.Zero && IsWindow(_hostWindow)) DestroyWindow(_hostWindow);
        _shutdown.Dispose();
    }

    private static IReadOnlyList<IntPtr> AdoptableWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetAncestor(hwnd, GaParent) == GetDesktopWindow() &&
                GetWindow(hwnd, GwOwner) == IntPtr.Zero && GetWindowText(hwnd).Trim().Length > 0 &&
                GetWindowRect(hwnd, out var rect) && rect.Right - rect.Left > 120 && rect.Bottom - rect.Top > 80)
                windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string GetWindowText(IntPtr hwnd) { var b = new char[512]; var n = GetWindowTextW(hwnd, b, b.Length); return n > 0 ? new string(b, 0, n) : string.Empty; }
    private static string GetClassName(IntPtr hwnd) { var b = new char[256]; var n = GetClassNameW(hwnd, b, b.Length); return n > 0 ? new string(b, 0, n) : string.Empty; }
    private static string GetProcessImageName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return string.Empty;
        try { var b = new char[1024]; var n = b.Length; return QueryFullProcessImageNameW(process, 0, b, ref n) ? Path.GetFileName(new string(b, 0, n)) : string.Empty; }
        finally { CloseHandle(process); }
    }
    private static void ThrowLastError(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

    private sealed record EmbedState(IntPtr Child, IntPtr OriginalParent, IntPtr OriginalStyle, IntPtr OriginalExStyle, Rect OriginalRect);
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass { public uint Style; public WindowProc WindowProc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? MenuName, ClassName; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public IntPtr Window; public uint Value; public IntPtr WParam, LParam; public uint Time; public Point Point; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr hwnd, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, int cursorId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, char[] buffer, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, char[] buffer, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] buffer, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
