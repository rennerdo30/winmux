using System.Runtime.InteropServices;

namespace WinMux.Shell;

/// <summary>Minimal Win32 for hosting foreign application windows. Everything here is Windows-only by nature.</summary>
internal static class Win32Interop
{
    private const string U = "user32.dll";
    private const string K = "kernel32.dll";

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const uint GW_OWNER = 4;
    public const uint GA_PARENT = 1;

    public const int WS_CHILD = 0x40000000;
    public const long WS_POPUP = 0x80000000L;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_BORDER = 0x00800000;
    public const int WS_DLGFRAME = 0x00400000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOCOPYBITS = 0x0100;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;
    public const uint MB_OK = 0x00000000;
    public const uint MB_ICONERROR = 0x00000010;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport(U, SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport(U)] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport(U)] public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);
    [DllImport(U)] public static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
    [DllImport(U)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
    [DllImport(U)] public static extern bool IsWindow(IntPtr h);
    [DllImport(U)] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport(U)] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport(U)] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport(U)] public static extern IntPtr GetDesktopWindow();
    [DllImport(U)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport(U)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport(U)] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport(U)] public static extern bool IsIconic(IntPtr h);
    [DllImport(U, SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport(U, SetLastError = true)] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr v);
    [DllImport(U, SetLastError = true)] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, char[] buf, int max);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, char[] buf, int max);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    [DllImport(K, SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport(K, SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport(K, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, char[] buf, ref int size);

    public const uint WM_CLOSE = 0x0010;

    // A reparented window does not repaint itself: it was never told its surface moved, so it
    // keeps whatever was on screen underneath. RDW_ALLCHILDREN matters because modern apps put
    // their real content in child windows.
    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_ERASE = 0x0004;
    public const uint RDW_FRAME = 0x0400;
    public const uint RDW_ALLCHILDREN = 0x0080;
    public const uint RDW_UPDATENOW = 0x0100;
    public const uint RDW_FULL = RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW;

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    /// <summary>The TRUE parent — <c>GetParent</c> returns the OWNER for a WS_POPUP window (ADR 0003).</summary>
    public static bool IsTopLevel(IntPtr h) => GetAncestor(h, GA_PARENT) == GetDesktopWindow();

    public static string GetWindowText(IntPtr h)
    {
        var buf = new char[512];
        int n = GetWindowTextW(h, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static string GetClassName(IntPtr h)
    {
        var buf = new char[256];
        int n = GetClassNameW(h, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static string GetProcessImageName(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        var proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (proc == IntPtr.Zero) return string.Empty;
        try
        {
            var buf = new char[1024];
            int size = buf.Length;
            return QueryFullProcessImageNameW(proc, 0, buf, ref size) ? Path.GetFileName(new string(buf, 0, size)) : string.Empty;
        }
        finally { CloseHandle(proc); }
    }

    /// <summary>
    /// The adoption rule from CLAUDE.md section 5, confirmed load-bearing by spike 2: visible,
    /// top-level, non-owned, with a real title and a plausible size. Notepad alone creates twelve
    /// top-level windows and this is what picks the right one.
    /// </summary>
    public static bool IsAdoptable(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        if (!IsTopLevel(h)) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        if (GetWindowText(h).Trim().Length == 0) return false;
        if (!GetWindowRect(h, out var r)) return false;
        return r.right - r.left > 120 && r.bottom - r.top > 80;
    }

    public static List<IntPtr> AdoptableWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsAdoptable(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
