namespace ReparentSpike;

/// <summary>
/// A window whose DPI awareness we control, so mixed-mode hosting can actually be tested.
///
/// The first attempt used __COMPAT_LAYER=DPIUNAWARE on charmap.exe and did nothing — the app's
/// manifest wins, and the process still reported PerMonitor. A test that cannot make the
/// condition it claims to test is not a test.
/// </summary>
internal static class GuineaPig
{
    public const string TitlePrefix = "WinMux DPI Guinea Pig";

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new(-1);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new(-2);

    public static int Run(Args a)
    {
        var mode = a.Str("awareness", "unaware").ToLowerInvariant();
        var ctx = mode switch
        {
            "unaware" => DPI_AWARENESS_CONTEXT_UNAWARE,
            "system" => DPI_AWARENESS_CONTEXT_SYSTEM_AWARE,
            _ => Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
        };
        var applied = Win32.SetProcessDpiAwarenessContext(ctx);

        var win = new NativeWindow(
            className: "WinMuxGuineaPig",
            title: TitlePrefix + " (" + mode + ")",
            style: Win32.WS_OVERLAPPEDWINDOW,
            exStyle: 0,
            x: 300, y: 300, w: 640, h: 420,
            parent: IntPtr.Zero,
            bg: Win32.Rgb(0x45, 0x47, 0x5a),
            fg: Win32.Rgb(0xf5, 0xc2, 0xe7));

        var ticks = 0;
        win.OnMessage = (msg, _, _) =>
        {
            if (msg != Win32.WM_TIMER) return null;
            ticks++;
            Win32.GetClientRect(win.Handle, out var rc);
            win.Text = "DPI guinea pig (" + mode + ")\n" +
                       "SetProcessDpiAwarenessContext applied: " + applied + "\n" +
                       "window dpi " + Win32.GetDpiForWindow(win.Handle) + "\n" +
                       "client " + rc.Width + "x" + rc.Height + "   ticks " + ticks;
            win.Repaint();
            return IntPtr.Zero;
        };

        win.Show();
        Win32.SetTimer(win.Handle, new IntPtr(1), 200, IntPtr.Zero);
        NativeWindow.Pump();
        return 0;
    }
}
