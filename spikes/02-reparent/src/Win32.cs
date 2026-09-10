using System.Runtime.InteropServices;

namespace ReparentSpike;

/// <summary>Hand-written P/Invoke for the spike. The product uses CsWin32; this is throwaway.</summary>
internal static class Win32
{
    // ---- styles ----
    public const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_BORDER = 0x00800000;
    public const int WS_DLGFRAME = 0x00400000;
    public const int WS_CLIPCHILDREN = 0x02000000;

    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const uint GW_OWNER = 4;

    // ---- messages ----
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_TIMER = 0x0113;

    // ---- SetWindowPos ----
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    // ---- SendMessageTimeout ----
    public const uint SMTO_NORMAL = 0x0000;
    public const uint SMTO_BLOCK = 0x0001;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNA = 8;

    // ---- DPI ----
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    public const int DPI_HOSTING_BEHAVIOR_MIXED = 1;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
        public int Width => right - left;
        public int Height => bottom - top;
        public override string ToString() => left + "," + top + " " + Width + "x" + Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    private const string U = "user32.dll";
    private const string K = "kernel32.dll";
    private const string G = "gdi32.dll";

    [DllImport(U, CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassW(ref WNDCLASSW c);

    [DllImport(U, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport(U, CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport(U, SetLastError = true)] public static extern bool DestroyWindow(IntPtr h);
    [DllImport(U)] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport(U)] public static extern bool UpdateWindow(IntPtr h);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
    public const uint PM_REMOVE = 0x0001;
    [DllImport(U)] public static extern bool TranslateMessage(ref MSG m);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport(U)] public static extern void PostQuitMessage(int code);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport(U, SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport(U)] public static extern IntPtr GetParent(IntPtr h);
    [DllImport(U, SetLastError = true)] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr val);
    [DllImport(U, SetLastError = true)] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport(U, SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport(U)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport(U)] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport(U)] public static extern bool IsWindow(IntPtr h);
    [DllImport(U)] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport(U)] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport(U)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
    [DllImport(U)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // Safe cross-process: returns the cached caption, does NOT send WM_GETTEXT to another process.
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, char[] buf, int max);

    [DllImport(U)] public static extern bool IsHungAppWindow(IntPtr h);

    [DllImport(U, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport(U)] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport(U)] public static extern bool KillTimer(IntPtr h, IntPtr id);
    [DllImport(U)] public static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
    [DllImport(U)] public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport(U)] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport(U)] public static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string s, int len, ref RECT r, uint fmt);

    [DllImport(U)] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport(U)] public static extern int SetThreadDpiHostingBehavior(int behavior);
    [DllImport(U)] public static extern uint GetDpiForWindow(IntPtr h);

    [DllImport(G)] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport(G)] public static extern bool DeleteObject(IntPtr o);
    [DllImport(G)] public static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport(G)] public static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport(K, CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport(K)] public static extern uint GetCurrentProcessId();

    public const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_LEFT = 0x0, DT_WORDBREAK = 0x10;
    public const int TRANSPARENT = 1;

    public static string GetWindowText(IntPtr h)
    {
        var buf = new char[512];
        int n = GetWindowTextW(h, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
