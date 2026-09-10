using System.Runtime.InteropServices;
using System.Text;

namespace ReparentSpike;

/// <summary>Additional interop spike 2 needs on top of the shared <see cref="Win32"/> surface.</summary>
internal static class Win32Extra
{
    private const string U = "user32.dll";
    private const string K = "kernel32.dll";
    private const string S = "shcore.dll";

    public const int ERROR_ACCESS_DENIED = 5;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport(U, CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr h, char[] buf, int max);

    [DllImport(U)] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport(U)] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport(U)] public static extern IntPtr GetDesktopWindow();

    public const uint GA_PARENT = 1;

    /// <summary>
    /// The TRUE parent. GetParent() is ambiguous: for a WS_POPUP window it returns the OWNER,
    /// not the parent, which made this spike report a false "embed failed" for Calculator
    /// (style 0x94CF0000 — WS_POPUP set). GetAncestor(GA_PARENT) has no such overload, but it
    /// returns the desktop rather than NULL for a top-level window.
    /// </summary>
    public static IntPtr TrueParent(IntPtr h)
    {
        var p = GetAncestor(h, GA_PARENT);
        return p == GetDesktopWindow() ? IntPtr.Zero : p;
    }

    public static bool IsTopLevel(IntPtr h) => TrueParent(h) == IntPtr.Zero;
    [DllImport(U)] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport(U)] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport(U)] public static extern bool EnumChildWindows(IntPtr parent, Win32.EnumWindowsProc cb, IntPtr param);

    [DllImport(S)] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport(S)] public static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);

    [DllImport(K, SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport(K, SetLastError = true)] public static extern bool CloseHandle(IntPtr h);

    [DllImport(K, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint flags, char[] buf, ref int size);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public static string GetClassName(IntPtr h)
    {
        var buf = new char[256];
        int n = GetClassNameW(h, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static uint GetMonitorDpi(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(mon, 0, out var x, out _) == 0 ? x : 0;
    }

    /// <summary>Image path of the process owning a window, or "" if it cannot be opened.</summary>
    public static string GetWindowExe(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (h == IntPtr.Zero) return string.Empty;
        try
        {
            var buf = new char[1024];
            int size = buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : string.Empty;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>PROCESS_DPI_AWARENESS of the window's process: Unaware / System / PerMonitor.</summary>
    public static string GetWindowDpiAwareness(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (h == IntPtr.Zero) return "unknown(access denied)";
        try
        {
            if (GetProcessDpiAwareness(h, out var a) != 0) return "unknown";
            return a switch { 0 => "Unaware", 1 => "System", 2 => "PerMonitor", _ => "?" + a };
        }
        finally { CloseHandle(h); }
    }

    /// <summary>True when the window's process cannot be opened for query — the usual UIPI signature.</summary>
    public static bool LooksElevated(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
        CloseHandle(h);
        return false;
    }

    public static string Describe(IntPtr h)
    {
        if (!Win32.IsWindow(h)) return "(dead window)";
        Win32.GetWindowRect(h, out var r);
        return "hwnd=0x" + h.ToString("X") +
               " class=" + GetClassName(h) +
               " title=\"" + Truncate(Win32.GetWindowText(h), 40) + "\"" +
               " rect=" + r +
               " dpi=" + GetMonitorDpi(h) +
               " awareness=" + GetWindowDpiAwareness(h);
    }

    public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
