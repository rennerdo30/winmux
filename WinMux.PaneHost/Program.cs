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
    string? WindowTitleContains,
    string? ProcessName,
    WindowMatchMode MatchMode,
    HostStrategy Strategy,
    int SettleMilliseconds,
    IntPtr OwnerWindow,
    IReadOnlySet<IntPtr> ExcludedWindows)
{
    public static LaunchSpec Parse(string[] args)
    {
        string? program = null;
        string? windowClass = null;
        string? windowTitleContains = null;
        string? processName = null;
        var matchMode = WindowMatchMode.ProcessName;
        var matchModeExplicit = false;
        var strategy = HostStrategy.Embed;
        var settleMilliseconds = 400;
        var ownerWindow = IntPtr.Zero;
        var arguments = new List<string>();
        var excluded = new HashSet<IntPtr>();
        var afterSeparator = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (!afterSeparator && args[i] == "--") { afterSeparator = true; continue; }
            if (!afterSeparator && args[i] == "--program" && ++i < args.Length) { program = args[i]; continue; }
            if (!afterSeparator && args[i] == "--window-class" && ++i < args.Length)
            {
                windowClass = args[i];
                if (!matchModeExplicit) matchMode = WindowMatchMode.ClassName;
                continue;
            }
            if (!afterSeparator && args[i] == "--window-title-contains" && ++i < args.Length)
            {
                windowTitleContains = args[i];
                continue;
            }
            if (!afterSeparator && args[i] == "--process-name" && ++i < args.Length)
            {
                processName = args[i];
                if (!matchModeExplicit) matchMode = WindowMatchMode.ProcessName;
                continue;
            }
            if (!afterSeparator && args[i] == "--match-mode")
            {
                if (++i >= args.Length) throw new ArgumentException("missing value for --match-mode");
                matchMode = args[i].ToLowerInvariant() switch
                {
                    "pid" => WindowMatchMode.Pid,
                    "process-name" => WindowMatchMode.ProcessName,
                    "class-name" => WindowMatchMode.ClassName,
                    _ => throw new ArgumentException("--match-mode must be pid, process-name, or class-name"),
                };
                matchModeExplicit = true;
                continue;
            }
            if (!afterSeparator && args[i] == "--strategy")
            {
                if (++i >= args.Length) throw new ArgumentException("missing value for --strategy");
                strategy = ParseStrategy(args[i]);
                continue;
            }
            if (!afterSeparator && args[i] == "--settle-ms")
            {
                if (++i >= args.Length || !int.TryParse(args[i], out settleMilliseconds) || settleMilliseconds < 0)
                    throw new ArgumentException("--settle-ms must be a non-negative integer");
                continue;
            }
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
        return new LaunchSpec(program, arguments, windowClass, windowTitleContains, processName, matchMode, strategy,
            settleMilliseconds, ownerWindow, excluded);
    }

    private static HostStrategy ParseStrategy(string value) => value.ToLowerInvariant() switch
    {
        "embed" => HostStrategy.Embed,
        "attach" => HostStrategy.Attach,
        _ => throw new ArgumentException("--strategy must be either embed or attach"),
    };
}

internal enum HostStrategy
{
    Embed,
    Attach,
}

internal enum WindowMatchMode
{
    Pid,
    ProcessName,
    ClassName,
}

internal static class HostProtocol
{
    public static string Strategy(HostStrategy strategy) =>
        $"STRATEGY={strategy.ToString().ToLowerInvariant()}";

    public static string Ready(IntPtr child) => $"READY={child.ToInt64()}";

    public static string Notice(string message) => "NOTICE=" + message;

    public static string EmbedFailure(int error) => error switch
    {
        5 => "ERROR=application is elevated or higher-integrity; WinMux cannot embed it " +
            "(win32=5, fallback=attach)",
        87 => "ERROR=application refused embedding (win32=87, fallback=attach)",
        0 => "ERROR=application did not become a child of PaneHost (win32=0, fallback=attach)",
        _ => $"ERROR=SetParent failed with Win32 error {error} (fallback=attach)",
    };
}

internal sealed class PaneHostWindow : IDisposable
{
    private const string WindowClassName = "WinMux.PaneHost.Window";
    private const int GwlStyle = -16, GwlExStyle = -20, SwHide = 0, SwShowNormal = 1, SwRestore = 9;
    private const int WsPopup = unchecked((int)0x80000000), WsVisible = 0x10000000;
    private const long WsPopupStyle = 0x80000000L, WsChildStyle = 0x40000000L;
    private const long ForeignFrameStyles = 0x00CF0000L;
    private const uint GaParent = 1, GwOwner = 4, WmClose = 0x0010, WmDestroy = 0x0002, WmShowWindow = 0x0018;
    private const uint WmMove = 0x0003, WmSize = 0x0005, WmActivate = 0x0006, WmWindowPosChanged = 0x0047;
    private const uint WmAppAdopt = 0x8001, WmAppClosePane = 0x8002;
    private const uint WmAppAttach = 0x8003, WmAppEmbed = 0x8004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpFrameChanged = 0x0020;
    private const uint SwpAsyncWindowPos = 0x4000;

    private readonly LaunchSpec _launch;
    private readonly WindowProc _windowProc;
    private readonly CancellationTokenSource _shutdown = new();
    private IntPtr _hostWindow;
    private IntPtr _childWindow;
    private uint _launchedProcessId;
    private HostedWindowState? _hosted;
    private bool _hostVisible = true;
    private bool _disposed;

    public PaneHostWindow(LaunchSpec launch) { _launch = launch; _windowProc = WindowProcImpl; }

    public void Run()
    {
        _ = SetThreadDpiHostingBehavior(new IntPtr(-3)); // DPI_HOSTING_BEHAVIOR_MIXED
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
            using var process = Process.Start(start) ??
                throw new InvalidOperationException("could not start foreign application");
            _launchedProcessId = (uint)process.Id;

            var stopwatch = Stopwatch.StartNew();
            var candidate = IntPtr.Zero;
            var candidateFoundAt = TimeSpan.Zero;
            var discoveryTimeout = TimeSpan.FromSeconds(12) +
                                   TimeSpan.FromMilliseconds(_launch.SettleMilliseconds);
            while (stopwatch.Elapsed < discoveryTimeout && !_shutdown.IsCancellationRequested)
            {
                var latest = AdoptableWindows().LastOrDefault(hwnd =>
                    !before.Contains(hwnd) && !_launch.ExcludedWindows.Contains(hwnd) && Matches(hwnd));
                if (latest != IntPtr.Zero)
                {
                    if (candidate != latest) candidateFoundAt = stopwatch.Elapsed;
                    candidate = latest;
                    if (stopwatch.Elapsed - candidateFoundAt >= TimeSpan.FromMilliseconds(_launch.SettleMilliseconds))
                    {
                        PostMessageW(_hostWindow, WmAppAdopt, candidate, IntPtr.Zero);
                        return;
                    }
                }
                else
                {
                    candidate = IntPtr.Zero;
                    candidateFoundAt = TimeSpan.Zero;
                }
                Thread.Sleep(100);
            }

            var existing = AdoptableWindows().LastOrDefault(hwnd =>
                before.Contains(hwnd) && !_launch.ExcludedWindows.Contains(hwnd) && Matches(hwnd));
            if (existing != IntPtr.Zero) { PostMessageW(_hostWindow, WmAppAdopt, existing, IntPtr.Zero); return; }
            if (!_shutdown.IsCancellationRequested)
                WriteProtocol($"ERROR=no matching window appeared within {discoveryTimeout.TotalSeconds:0.#} seconds");
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
                        break;
                    case "CLOSE":
                        PostMessageW(_hostWindow, WmAppClosePane, IntPtr.Zero, IntPtr.Zero);
                        break;
                    case "STRATEGY=ATTACH":
                        PostMessageW(_hostWindow, WmAppAttach, IntPtr.Zero, IntPtr.Zero);
                        break;
                    case "STRATEGY=EMBED":
                        PostMessageW(_hostWindow, WmAppEmbed, IntPtr.Zero, IntPtr.Zero);
                        break;
                }
            }

            // Redirected stdin closes when the shell crashes. Restore autonomously so the app
            // cannot remain an invisible orphaned child after its owner disappears.
            while (!_shutdown.IsCancellationRequested && IsWindow(_hostWindow))
            {
                PostMessageW(_hostWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(1_000, _shutdown.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool Matches(IntPtr hwnd)
    {
        var identityMatches = _launch.MatchMode switch
        {
            WindowMatchMode.Pid => GetWindowProcessId(hwnd) == _launchedProcessId,
            WindowMatchMode.ClassName => !string.IsNullOrWhiteSpace(_launch.WindowClass) &&
                                         GetClassName(hwnd).Equals(
                                             _launch.WindowClass,
                                             StringComparison.OrdinalIgnoreCase),
            _ => GetProcessImageName(hwnd).Equals(
                _launch.ProcessName ?? Path.GetFileName(_launch.Program),
                StringComparison.OrdinalIgnoreCase),
        };
        return identityMatches &&
               (string.IsNullOrWhiteSpace(_launch.WindowTitleContains) ||
                GetWindowText(hwnd).Contains(_launch.WindowTitleContains, StringComparison.OrdinalIgnoreCase));
    }

    private static uint GetWindowProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    private void HostApplication(IntPtr child)
    {
        if (!IsWindow(child)) return;
        if (!GetWindowRect(child, out var originalRect)) ThrowLastError("GetWindowRect");
        var original = new HostedWindowState(child, GetAncestor(child, GaParent), GetWindowLongPtrW(child, GwlStyle),
            GetWindowLongPtrW(child, GwlExStyle), originalRect, _launch.Strategy);

        if (IsIconic(child)) ShowWindow(child, SwRestore);
        if (_launch.Strategy == HostStrategy.Attach)
        {
            Attach(original with { Strategy = HostStrategy.Attach });
            return;
        }

        Embed(original with { Strategy = HostStrategy.Embed });
    }

    private void Embed(HostedWindowState state)
    {
        _hosted = state;
        var child = state.Child;
        var childStyle = ((state.OriginalStyle.ToInt64() & 0xffffffffL) | WsChildStyle) &
                         ~(WsPopupStyle | ForeignFrameStyles);
        Marshal.SetLastPInvokeError(0);
        SetWindowLongPtrW(child, GwlStyle, new IntPtr(childStyle));
        var styleError = Marshal.GetLastPInvokeError();
        if ((GetWindowLongPtrW(child, GwlStyle).ToInt64() & 0xffffffffL) != childStyle)
        {
            if (RestoreForeignWindow())
            {
                WriteProtocol(HostProtocol.Notice(
                    $"application style could not be prepared for embedding (win32={styleError}, fallback=attach)"));
                Attach(state with { Strategy = HostStrategy.Attach });
            }
            return;
        }

        Marshal.SetLastPInvokeError(0);
        var oldParent = SetParent(child, _hostWindow);
        var error = Marshal.GetLastPInvokeError();
        if ((oldParent == IntPtr.Zero && error != 0) || GetAncestor(child, GaParent) != _hostWindow)
        {
            if (!RestoreForeignWindow()) return;
            var reason = HostProtocol.EmbedFailure(error)["ERROR=".Length..];
            WriteProtocol(HostProtocol.Notice(reason));
            Attach(state with { Strategy = HostStrategy.Attach });
            return;
        }

        _childWindow = child;
        ResizeChild();
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        WriteProtocol(HostProtocol.Strategy(HostStrategy.Embed));
        WriteProtocol(HostProtocol.Ready(child));
    }

    private void Attach(HostedWindowState state)
    {
        _hosted = state;
        _childWindow = state.Child;
        if (!PositionAttachedWindow())
        {
            if (RestoreForeignWindow())
                WriteProtocol("ERROR=Windows refused to position the application in attach mode");
            return;
        }

        WriteProtocol(HostProtocol.Strategy(HostStrategy.Attach));
        WriteProtocol(HostProtocol.Ready(state.Child));
    }

    private void SwitchStrategy(HostStrategy strategy)
    {
        if (_hosted is not { } current || !IsWindow(current.Child))
        {
            WriteProtocol("ERROR=no hosted application is available to switch");
            return;
        }
        if (current.Strategy == strategy)
        {
            WriteProtocol(HostProtocol.Strategy(strategy));
            WriteProtocol(HostProtocol.Ready(current.Child));
            return;
        }

        if (!RestoreForeignWindow(restoreRect: false)) return;
        if (strategy == HostStrategy.Attach)
            Attach(current with { Strategy = HostStrategy.Attach });
        else
            Embed(current with { Strategy = HostStrategy.Embed });
    }

    private void ResizeChild()
    {
        if (_hosted?.Strategy == HostStrategy.Attach)
        {
            PositionAttachedWindow();
            return;
        }

        if (_childWindow == IntPtr.Zero || !IsWindow(_childWindow) || !GetClientRect(_hostWindow, out var rect)) return;
        SetWindowPos(_childWindow, IntPtr.Zero, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    private bool PositionAttachedWindow()
    {
        if (_childWindow == IntPtr.Zero || !IsWindow(_childWindow)) return false;
        if (!_hostVisible)
        {
            ShowWindow(_childWindow, SwHide);
            return true;
        }
        if (!GetWindowRect(_hostWindow, out var rect))
        {
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        return SetWindowPos(_childWindow, IntPtr.Zero, rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            SwpNoActivate | SwpShowWindow | SwpAsyncWindowPos);
    }

    private bool RestoreForeignWindow(bool restoreRect = true)
    {
        if (_hosted is not { } hosted) return true;
        if (!IsWindow(hosted.Child))
        {
            _hosted = null;
            _childWindow = IntPtr.Zero;
            return true;
        }

        if (hosted.Strategy == HostStrategy.Embed)
        {
            Marshal.SetLastPInvokeError(0);
            SetParent(hosted.Child, hosted.OriginalParent);
            var parentError = Marshal.GetLastPInvokeError();
            if (GetAncestor(hosted.Child, GaParent) != hosted.OriginalParent)
            {
                WriteProtocol($"ERROR=could not detach application from PaneHost (win32={parentError})");
                return false;
            }

            Marshal.SetLastPInvokeError(0);
            SetWindowLongPtrW(hosted.Child, GwlStyle, hosted.OriginalStyle);
            var styleError = Marshal.GetLastPInvokeError();
            if (GetWindowLongPtrW(hosted.Child, GwlStyle) != hosted.OriginalStyle)
            {
                WriteProtocol($"ERROR=could not restore application window style (win32={styleError})");
                return false;
            }

            Marshal.SetLastPInvokeError(0);
            SetWindowLongPtrW(hosted.Child, GwlExStyle, hosted.OriginalExStyle);
            var exStyleError = Marshal.GetLastPInvokeError();
            if (GetWindowLongPtrW(hosted.Child, GwlExStyle) != hosted.OriginalExStyle)
            {
                WriteProtocol($"ERROR=could not restore application extended style (win32={exStyleError})");
                return false;
            }
        }

        if (restoreRect)
        {
            var restoreFlags = SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow;
            if (hosted.Strategy == HostStrategy.Attach) restoreFlags |= SwpAsyncWindowPos;
            if (!SetWindowPos(hosted.Child, IntPtr.Zero, hosted.OriginalRect.Left, hosted.OriginalRect.Top,
                    hosted.OriginalRect.Right - hosted.OriginalRect.Left,
                    hosted.OriginalRect.Bottom - hosted.OriginalRect.Top,
                    restoreFlags))
            {
                WriteProtocol($"ERROR=could not restore application rectangle (win32={Marshal.GetLastPInvokeError()})");
                return false;
            }
        }
        _hosted = null;
        _childWindow = IntPtr.Zero;
        return true;
    }

    private IntPtr WindowProcImpl(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmAppAdopt:
                try { HostApplication(wParam); } catch (Exception ex) { WriteProtocol("ERROR=" + ex.Message); }
                return IntPtr.Zero;
            case WmAppAttach:
                try { SwitchStrategy(HostStrategy.Attach); } catch (Exception ex) { WriteProtocol("ERROR=" + ex.Message); }
                return IntPtr.Zero;
            case WmAppEmbed:
                try { SwitchStrategy(HostStrategy.Embed); } catch (Exception ex) { WriteProtocol("ERROR=" + ex.Message); }
                return IntPtr.Zero;
            case WmMove:
            case WmSize:
            case WmWindowPosChanged:
                ResizeChild();
                break;
            case WmShowWindow:
                _hostVisible = wParam != IntPtr.Zero;
                if (_hosted?.Strategy == HostStrategy.Attach) PositionAttachedWindow();
                break;
            case WmActivate:
                if (_hosted?.Strategy == HostStrategy.Attach && (wParam.ToInt64() & 0xffff) != 0)
                    PositionAttachedWindow();
                break;
            case WmAppClosePane:
                var child = _childWindow;
                if (!RestoreForeignWindow()) return IntPtr.Zero;
                if (child != IntPtr.Zero && IsWindow(child)) PostMessageW(child, WmClose, IntPtr.Zero, IntPtr.Zero);
                WriteProtocol("CLOSED");
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmClose:
                if (!RestoreForeignWindow()) return IntPtr.Zero;
                WriteProtocol("DETACHED");
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                RestoreForeignWindow();
                PostQuitMessage(0);
                return IntPtr.Zero;
            default: return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void WriteProtocol(string line) { Console.Out.WriteLine(line); Console.Out.Flush(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        var restored = RestoreForeignWindow();
        if (restored && _hostWindow != IntPtr.Zero && IsWindow(_hostWindow)) DestroyWindow(_hostWindow);
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

    private sealed record HostedWindowState(
        IntPtr Child,
        IntPtr OriginalParent,
        IntPtr OriginalStyle,
        IntPtr OriginalExStyle,
        Rect OriginalRect,
        HostStrategy Strategy);
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
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiHostingBehavior(IntPtr value);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] buffer, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
