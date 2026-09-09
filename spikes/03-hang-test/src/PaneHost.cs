using System.Diagnostics;

namespace HangSpike;

/// <summary>
/// The out-of-process pane host from CLAUDE.md section 5. Owns a borderless window,
/// launches the foreign app and reparents it into that window. If the app wedges,
/// this process wedges with it — that is the trade the design is buying.
/// </summary>
internal static class PaneHost
{
    public static int Run(Args a)
    {
        var logDir = a.Str("logdir", ".");
        Log.Init("panehost", logDir);

        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        // Required to host an app with different DPI awareness (CLAUDE.md section 5).
        var prevBehavior = Win32.SetThreadDpiHostingBehavior(Win32.DPI_HOSTING_BEHAVIOR_MIXED);
        Log.Line("SetThreadDpiHostingBehavior(MIXED) previous=" + prevBehavior);

        int x = a.Int("x", 0), y = a.Int("y", 0), w = a.Int("w", 520), h = a.Int("h", 340);

        var win = new NativeWindow(
            className: "WinMuxSpikePaneHost",
            title: "WinMux PaneHost " + Environment.ProcessId,
            style: Win32.WS_POPUP | Win32.WS_CLIPCHILDREN,
            exStyle: Win32.WS_EX_TOOLWINDOW,
            x: x, y: y, w: w, h: h,
            parent: IntPtr.Zero,
            bg: Win32.Rgb(0x31, 0x32, 0x44),
            fg: Win32.Rgb(0xa6, 0xe3, 0xa1));

        win.Show();

        // Launch the app this host is responsible for.
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.ArgumentList.Add("--role"); psi.ArgumentList.Add("hangapp");
        psi.ArgumentList.Add("--logdir"); psi.ArgumentList.Add(logDir);
        psi.ArgumentList.Add("--x"); psi.ArgumentList.Add("2000");
        psi.ArgumentList.Add("--y"); psi.ArgumentList.Add("2000");
        var app = Process.Start(psi)!;
        Log.Line("launched hangapp pid=" + app.Id);

        var appHwnd = WindowFinder.WaitForMainWindow(app.Id, HangApp.TitlePrefix, 10000);
        if (appHwnd == IntPtr.Zero)
        {
            Log.Line("FATAL: hangapp window never appeared");
            return 2;
        }

        var rec = Embedding.Embed(appHwnd, win.Handle, w, h);
        EmbedRecord? live = rec;

        // Detach on any orderly exit. A hard kill skips this — which is the point of test T5.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (live != null) { Embedding.Detach(live); live = null; }
        };

        var cmdPath = Ipc.CmdPath(logDir, Environment.ProcessId);
        var ticks = 0;

        win.OnMessage = (msg, _, _) =>
        {
            if (msg != Win32.WM_TIMER) return null;
            ticks++;
            win.Text = "PaneHost pid " + Environment.ProcessId + "  ticks " + ticks;
            win.Repaint();

            var cmd = Ipc.TryRead(cmdPath);
            if (cmd == null) return IntPtr.Zero;
            File.Delete(cmdPath);
            var d = Ipc.Parse(cmd);

            if (d.ContainsKey("detach"))
            {
                Log.Line("command: detach");
                if (live != null) { Embedding.Detach(live); live = null; }
            }
            if (d.ContainsKey("quit"))
            {
                Log.Line("command: quit");
                if (live != null) { Embedding.Detach(live); live = null; }
                Win32.PostMessageW(win.Handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            return IntPtr.Zero;
        };

        Win32.SetTimer(win.Handle, new IntPtr(1), 100, IntPtr.Zero);

        Ipc.Write(Ipc.ReadyPath(logDir, Environment.ProcessId),
            "role=panehost\npid=" + Environment.ProcessId +
            "\nhwnd=" + win.Handle.ToInt64() +
            "\napppid=" + app.Id +
            "\napphwnd=" + appHwnd.ToInt64() + "\n");

        Log.Line("ready hosthwnd=0x" + win.Handle.ToString("X") + " apphwnd=0x" + appHwnd.ToString("X"));
        NativeWindow.Pump();

        if (live != null) { Embedding.Detach(live); live = null; }
        Log.Line("exiting");
        return 0;
    }
}
