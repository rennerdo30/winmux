using System.Diagnostics;
using System.Text;

namespace ReparentSpike;

/// <summary>Timestamped log to stdout and to a per-process file, so the three roles can be correlated.</summary>
internal static class Log
{
    private static StreamWriter? _file;
    private static string _role = "?";
    private static readonly object Gate = new();
    public static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Init(string role, string logDir)
    {
        _role = role;
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, role + "-" + Environment.ProcessId + ".log");
        _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Line("=== " + role + " pid=" + Environment.ProcessId + " ===");
    }

    public static void Line(string msg)
    {
        var stamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        var s = stamp + " [" + _role + "/" + Environment.ProcessId + "] " + msg;
        lock (Gate)
        {
            Console.WriteLine(s);
            _file?.WriteLine(s);
        }
    }
}

/// <summary>
/// Deliberately crude IPC: files in the log directory. The product uses named pipes
/// (CLAUDE.md section 5); for this spike the channel is not what is under test, and a
/// file drop cannot itself deadlock against a hung UI thread.
/// </summary>
internal static class Ipc
{
    public static string ReadyPath(string dir, int pid) => Path.Combine(dir, "ready-" + pid + ".txt");
    public static string CmdPath(string dir, int pid) => Path.Combine(dir, "cmd-" + pid + ".txt");

    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
    }

    public static Dictionary<string, string> Parse(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in s.Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i > 0) d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return d;
    }
}

/// <summary>Minimal registered-class window with a text-painting WndProc.</summary>
internal sealed class NativeWindow
{
    private readonly Win32.WndProcDelegate _proc; // must stay rooted for the window's lifetime
    private readonly IntPtr _bgBrush;
    private readonly uint _fg;
    public IntPtr Handle { get; private set; }
    public string Text = string.Empty;

    /// <summary>Return non-null to handle the message and skip DefWindowProc.</summary>
    public Func<uint, IntPtr, IntPtr, IntPtr?>? OnMessage;

    public NativeWindow(string className, string? title, int style, int exStyle,
                        int x, int y, int w, int h, IntPtr parent, uint bg, uint fg)
    {
        _proc = WndProc;
        _bgBrush = Win32.CreateSolidBrush(bg);
        _fg = fg;

        var inst = Win32.GetModuleHandleW(null);
        var wc = new Win32.WNDCLASSW
        {
            style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = inst,
            hbrBackground = _bgBrush,
            lpszClassName = className,
        };
        if (Win32.RegisterClassW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassW failed for " + className +
                ": " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        Handle = Win32.CreateWindowExW(exStyle, className, title, style, x, y, w, h, parent, IntPtr.Zero, inst, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowExW failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        var handled = OnMessage?.Invoke(msg, w, l);
        if (handled.HasValue) return handled.Value;

        switch (msg)
        {
            case Win32.WM_PAINT:
            {
                var hdc = Win32.BeginPaint(h, out var ps);
                Win32.GetClientRect(h, out var rc);
                Win32.FillRect(hdc, ref rc, _bgBrush);
                Win32.SetBkMode(hdc, Win32.TRANSPARENT);
                Win32.SetTextColor(hdc, _fg);
                var pad = rc; pad.left += 12; pad.top += 10; pad.right -= 12;
                var t = Text;
                Win32.DrawTextW(hdc, t, t.Length, ref pad, Win32.DT_LEFT | Win32.DT_WORDBREAK);
                Win32.EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    public void Show()
    {
        Win32.ShowWindow(Handle, Win32.SW_SHOW);
        Win32.UpdateWindow(Handle);
    }

    public void Repaint() => Win32.InvalidateRect(Handle, IntPtr.Zero, true);

    /// <summary>Standard message loop. Returns when WM_QUIT is received.</summary>
    public static void Pump()
    {
        while (true)
        {
            int r = Win32.GetMessageW(out var m, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) return;
            Win32.TranslateMessage(ref m);
            Win32.DispatchMessageW(ref m);
        }
    }
}

internal static class WindowFinder
{
    /// <summary>Adopt only visible, top-level, non-owned windows with a real title (CLAUDE.md section 5).</summary>
    public static IntPtr FindMainWindow(int pid, string titlePrefix)
    {
        IntPtr found = IntPtr.Zero;
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out var wpid);
            if (wpid != (uint)pid) return true;
            if (!Win32.IsWindowVisible(h)) return true;
            if (Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) return true; // owned => tool/splash
            var title = Win32.GetWindowText(h);
            if (title.Length == 0) return true;
            if (titlePrefix.Length > 0 && !title.StartsWith(titlePrefix, StringComparison.Ordinal)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr WaitForMainWindow(int pid, string titlePrefix, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var h = FindMainWindow(pid, titlePrefix);
            if (h != IntPtr.Zero) return h;
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }
}

/// <summary>Records the state needed to undo an embed exactly (CLAUDE.md section 5).</summary>
internal sealed class EmbedRecord
{
    public IntPtr Child;
    public IntPtr OriginalParent;
    public IntPtr OriginalStyle;
    public IntPtr OriginalExStyle;
    public Win32.RECT OriginalRect;

    public static EmbedRecord Capture(IntPtr child)
    {
        Win32.GetWindowRect(child, out var rc);
        return new EmbedRecord
        {
            Child = child,
            OriginalParent = Win32.GetParent(child),
            OriginalStyle = Win32.GetWindowLongPtrW(child, Win32.GWL_STYLE),
            OriginalExStyle = Win32.GetWindowLongPtrW(child, Win32.GWL_EXSTYLE),
            OriginalRect = rc,
        };
    }

    public string Describe() =>
        "parent=0x" + OriginalParent.ToString("X") +
        " style=0x" + OriginalStyle.ToInt64().ToString("X") +
        " ex=0x" + OriginalExStyle.ToInt64().ToString("X") +
        " rect=" + OriginalRect;
}

internal static class Embedding
{
    /// <summary>GetLastError captured immediately after the most recent SetParent.</summary>
    public static int LastSetParentError;

    /// <summary>Reparent <paramref name="child"/> into <paramref name="host"/>, fixing styles as SetParent does not.</summary>
    public static EmbedRecord Embed(IntPtr child, IntPtr host, int w, int h)
    {
        var rec = EmbedRecord.Capture(child);
        Log.Line("embed: capturing original " + rec.Describe());

        // Read the error IMMEDIATELY. The style/position calls below clobber it, which made this
        // spike report "last error 0" for windows UIPI had actually refused.
        var ret = Win32.SetParent(child, host);
        LastSetParentError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        Log.Line("embed: SetParent returned 0x" + ret.ToString("X") + ", GetLastError=" + LastSetParentError +
                 (LastSetParentError == 5 ? " (ERROR_ACCESS_DENIED — UIPI)" : ""));

        long style = rec.OriginalStyle.ToInt64();
        style |= Win32.WS_CHILD;
        style &= ~0x80000000L; // clear WS_POPUP
        style &= ~(long)(Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_MINIMIZEBOX |
                         Win32.WS_MAXIMIZEBOX | Win32.WS_SYSMENU | Win32.WS_BORDER | Win32.WS_DLGFRAME);
        Win32.SetWindowLongPtrW(child, Win32.GWL_STYLE, new IntPtr(style));

        Win32.SetWindowPos(child, IntPtr.Zero, 0, 0, w, h,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
        Log.Line("embed: child 0x" + child.ToString("X") + " -> host 0x" + host.ToString("X"));
        return rec;
    }

    /// <summary>Undo an embed exactly. Must work on clean exit, on crash and on panic hotkey.</summary>
    public static void Detach(EmbedRecord rec)
    {
        if (!Win32.IsWindow(rec.Child)) { Log.Line("detach: child window is gone, nothing to restore"); return; }
        Win32.SetParent(rec.Child, rec.OriginalParent);
        Win32.SetWindowLongPtrW(rec.Child, Win32.GWL_STYLE, rec.OriginalStyle);
        Win32.SetWindowLongPtrW(rec.Child, Win32.GWL_EXSTYLE, rec.OriginalExStyle);
        var r = rec.OriginalRect;
        Win32.SetWindowPos(rec.Child, IntPtr.Zero, r.left, r.top, r.Width, r.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
        Log.Line("detach: restored " + rec.Describe());
    }
}

internal sealed class Args
{
    private readonly Dictionary<string, string> _v = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = argv[i][2..];
            var val = (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)) ? argv[++i] : "true";
            _v[key] = val;
        }
    }

    public string Str(string k, string def) => _v.TryGetValue(k, out var v) ? v : def;
    public int Int(string k, int def) => _v.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : def;
    public bool Flag(string k) => _v.ContainsKey(k);
}
