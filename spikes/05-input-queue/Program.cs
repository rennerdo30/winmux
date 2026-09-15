using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputQueueSpike;

/// <summary>
/// Does input-queue attachment actually starve the shell of input?
///
/// CLAUDE.md section 5 states the trap: `SetParent` across processes attaches the two threads'
/// input queues, transitively, so one hung application hangs everyone attached to it. ADR 0001
/// recorded it as *true and unproven* — spike 3 chained shell → host → app and the shell stayed
/// responsive, but its harness measured message-loop liveness, which is not the same thing as
/// being able to receive a keystroke. The constraint that follows ("never `SetParent` a pane host
/// into the shell window") has been load-bearing for three phases on an unmeasured claim.
///
/// This measures the thing itself: synthesized keyboard input, delivered to a window whose input
/// queue is attached to a wedged process, with the latency recorded.
///
/// Two runs:
///   --mode detached   host and app are unrelated top-level windows (the shipping topology)
///   --mode attached   the app's window is SetParent'd into the host's (the forbidden one)
///
/// Both wedge the app halfway through and keep typing.
/// </summary>
internal static class Program
{
    private const int WedgeSeconds = 6;
    private const int KeysPerPhase = 10;
    private const int KeyIntervalMs = 100;

    private static int Main(string[] args)
    {
        if (args.Contains("--app")) return RunApp();

        var attached = args.Contains("attached");
        Console.WriteLine($"=== input-queue spike: {(attached ? "ATTACHED (SetParent)" : "DETACHED (separate top-levels)")}");

        using var host = new HostWindow();
        var app = StartApp(out var appWindow);

        try
        {
            if (attached)
            {
                // The topology ADR 0001 forbids. Capture the error on the very next line: a failed
                // SetParent returns NULL and any other Win32 call clobbers GetLastError (ADR 0003).
                var previous = Native.SetParent(appWindow, host.Handle);
                var error = Marshal.GetLastWin32Error();
                if (previous == nint.Zero && error != 0)
                {
                    Console.WriteLine($"SetParent failed with {error}; cannot run the attached case.");
                    return 2;
                }

                Console.WriteLine("SetParent done: the app's thread and ours now share an input queue.");
            }

            if (!host.Activate())
            {
                // Without the foreground, synthesized input goes somewhere else entirely and every
                // number below would be a measurement of nothing. Spike 3's lesson, and ADR 0016's.
                Console.WriteLine("FAILED: could not take the foreground, so no input can be measured.");
                return 3;
            }

            Console.WriteLine("host window has the foreground.");

            var before = Measure(host, "app healthy");
            Console.WriteLine($"  received {before.Received}/{KeysPerPhase}, worst latency {before.WorstMs:F1} ms");

            if (before.Received == 0)
            {
                Console.WriteLine("FAILED: no input arrived even with the app healthy — the harness is wrong, not the OS.");
                return 4;
            }

            Console.WriteLine($"wedging the app for {WedgeSeconds}s...");
            Native.PostMessage(appWindow, Native.WmWedge, nint.Zero, nint.Zero);
            Thread.Sleep(300);

            var during = Measure(host, "app wedged");
            Console.WriteLine($"  received {during.Received}/{KeysPerPhase}, worst latency {during.WorstMs:F1} ms");

            // Receiving input is only half of it. The shell also *asks* for focus — when a pane is
            // selected, when a window is restored — and doing that while sharing an input queue
            // with a wedged thread is where a block would actually show up.
            var focusClock = Stopwatch.StartNew();
            var refocused = host.Activate();
            focusClock.Stop();
            Console.WriteLine(
                $"  taking focus while wedged: {focusClock.Elapsed.TotalMilliseconds:F0} ms, " +
                $"{(refocused ? "succeeded" : "FAILED")}");

            Console.WriteLine();
            var blockedOnFocus = focusClock.Elapsed.TotalMilliseconds > 1000;
            Console.WriteLine(during.Received == 0
                ? "STARVED: no input arrived while the app was wedged."
                : during.WorstMs > 1000
                    ? $"DEGRADED: input still arrived, but took up to {during.WorstMs:F0} ms."
                    : blockedOnFocus
                        ? $"INPUT FINE, FOCUS BLOCKED: taking focus cost {focusClock.Elapsed.TotalMilliseconds:F0} ms."
                        : "UNAFFECTED: input kept arriving, and taking focus did not block.");

            return 0;
        }
        finally
        {
            // Detach before killing, always. Spike 3 measured that killing a host still owning a
            // reparented window destroys that window (ADR 0001).
            if (attached && Native.IsWindow(appWindow)) Native.SetParent(appWindow, nint.Zero);
            try { if (!app.HasExited) app.Kill(); } catch { /* already gone */ }
        }
    }

    private static Phase Measure(HostWindow host, string label)
    {
        Console.WriteLine($"typing {KeysPerPhase} keys, {label}:");
        host.Reset();

        var worst = 0.0;
        for (var i = 0; i < KeysPerPhase; i++)
        {
            var sent = Stopwatch.GetTimestamp();
            host.ExpectAt(sent);
            Native.SendKey();
            Thread.Sleep(KeyIntervalMs);
            worst = Math.Max(worst, host.WorstLatencyMs);
        }

        // One more drain: a key delivered late still counts as delivered.
        Thread.Sleep(500);
        return new Phase(host.Received, Math.Max(worst, host.WorstLatencyMs));
    }

    private readonly record struct Phase(int Received, double WorstMs);

    private static Process StartApp(out nint window)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var process = Process.Start(new ProcessStartInfo(self, "--app")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("could not start the app");

        var line = process.StandardOutput.ReadLine();
        if (line is null || !nint.TryParse(line, out window))
            throw new InvalidOperationException("the app did not report its window: " + line);

        return process;
    }

    /// <summary>The application being hosted: pumps, then stops pumping on command.</summary>
    private static int RunApp()
    {
        using var window = new AppWindow();
        Console.WriteLine(window.Handle);
        Console.Out.Flush();
        window.Pump();
        return 0;
    }
}
