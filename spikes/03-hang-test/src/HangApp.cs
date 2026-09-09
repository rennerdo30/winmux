using System.Diagnostics;

namespace HangSpike;

/// <summary>
/// Stand-in for a misbehaving foreign app. A plain top-level Win32 window that,
/// on command, stops pumping messages by sleeping on its UI thread.
/// </summary>
internal static class HangApp
{
    public const string TitlePrefix = "WinMux HangApp";

    public static int Run(Args a)
    {
        var logDir = a.Str("logdir", ".");
        Log.Init("hangapp", logDir);

        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var win = new NativeWindow(
            className: "WinMuxSpikeHangApp",
            title: TitlePrefix + " " + Environment.ProcessId,
            style: Win32.WS_OVERLAPPEDWINDOW,
            exStyle: 0,
            x: a.Int("x", 100), y: a.Int("y", 100),
            w: a.Int("w", 520), h: a.Int("h", 340),
            parent: IntPtr.Zero,
            bg: Win32.Rgb(0x1e, 0x1e, 0x2e),
            fg: Win32.Rgb(0xcd, 0xd6, 0xf4));

        var cmdPath = Ipc.CmdPath(logDir, Environment.ProcessId);
        var ticks = 0;
        var hangCount = 0;
        var sw = Stopwatch.StartNew();

        win.OnMessage = (msg, _, _) =>
        {
            if (msg != Win32.WM_TIMER) return null;

            ticks++;
            win.Text = "HangApp  pid " + Environment.ProcessId + "\n" +
                       "hwnd 0x" + win.Handle.ToString("X") + "\n" +
                       "ticks " + ticks + "   uptime " + sw.Elapsed.TotalSeconds.ToString("F1") + "s\n" +
                       "hangs served: " + hangCount;
            win.Repaint();

            var cmd = Ipc.TryRead(cmdPath);
            if (cmd != null)
            {
                File.Delete(cmdPath);
                var d = Ipc.Parse(cmd);
                if (d.TryGetValue("hang", out var msStr) && int.TryParse(msStr, out var ms))
                {
                    hangCount++;
                    Log.Line("HANG-BEGIN " + ms + "ms (UI thread stops pumping now)");
                    // This is the whole point: block the UI thread so it never returns to GetMessage.
                    Thread.Sleep(ms);
                    Log.Line("HANG-END");
                }
                else if (d.ContainsKey("quit"))
                {
                    Win32.PostMessageW(win.Handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            return IntPtr.Zero;
        };

        win.Show();
        Win32.SetTimer(win.Handle, new IntPtr(1), 100, IntPtr.Zero);

        Ipc.Write(Ipc.ReadyPath(logDir, Environment.ProcessId),
            "role=hangapp\npid=" + Environment.ProcessId + "\nhwnd=" + win.Handle.ToInt64() + "\n");

        Log.Line("ready hwnd=0x" + win.Handle.ToString("X") + " dpi=" + Win32.GetDpiForWindow(win.Handle));
        NativeWindow.Pump();
        Log.Line("exiting");
        return 0;
    }
}
