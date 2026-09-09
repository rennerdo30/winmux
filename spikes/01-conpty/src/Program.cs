using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ConPtySpike;

/// <summary>
/// Spike 1, stage A: what does ConPTY itself cost, before any rendering library is involved?
/// Measures spawn, keystroke echo latency, bulk throughput and resize correctness.
/// </summary>
internal static class Program
{
    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (argv.Length > 0 && argv[0] == "--diag") return Diag(argv.Contains("--freeconsole"));
        if (argv.Length > 0 && argv[0] == "--stageb") return StageB.Run();
        if (argv.Length > 0 && argv[0] == "--control") return Control.RunAsync().GetAwaiter().GetResult();
        var shells = argv.Length > 0 ? argv : ["pwsh", "powershell", "cmd"];

        Console.WriteLine("ConPTY spike — stage A (plumbing only, no rendering)");
        Console.WriteLine("OS " + Environment.OSVersion.Version + "   .NET " + Environment.Version);
        Console.WriteLine(new string('=', 78));

        var failures = 0;
        foreach (var shell in shells)
        {
            try { if (!RunShell(shell)) failures++; }
            catch (Exception e)
            {
                Console.WriteLine("  " + shell + ": FAILED — " + e.Message);
                failures++;
            }
            Console.WriteLine();
        }
        return failures;
    }

    /// <summary>Minimal end-to-end check: does one echoed marker come back through the pipe at all?</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool FreeConsole();

    private static int Diag(bool freeConsole)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "winmux-spike1-diag.txt");
        var log = new StringBuilder();
        void W(string s) { log.AppendLine(s); if (!freeConsole) Console.WriteLine(s); }

        W("sizeof(STARTUPINFOW)=" + Marshal.SizeOf<Native.STARTUPINFOW>() +
          "  sizeof(STARTUPINFOEXW)=" + Marshal.SizeOf<Native.STARTUPINFOEXW>() + "  (expect 104 / 112)");

        if (freeConsole)
        {
            var freed = FreeConsole();
            W("FreeConsole() -> " + freed + "  (host now has no console of its own)");
        }
        W("diag: spawning `cmd.exe /c echo HELLO_PTY_MARKER` into a pseudoconsole");
        var got = new StringBuilder();
        var sawEof = false;

        using (var pty = new PtySession("cmd.exe /c echo HELLO_PTY_MARKER", 80, 25))
        {
            pty.DataReceived += (b, n) =>
            {
                var s = Encoding.UTF8.GetString(b, 0, n);
                lock (got) got.Append(s);
                W("      chunk " + n + " bytes: " + Escape(s));
            };
            pty.Eof += () => { sawEof = true; W("      EOF on output pipe"); };
            pty.WaitForExit(5000);
            Thread.Sleep(800);
        }

        var all = got.ToString();
        var ok = all.Contains("HELLO_PTY_MARKER", StringComparison.Ordinal);
        W("      total " + all.Length + " chars, eof=" + sawEof);
        W("      MARKER CAME THROUGH THE PIPE: " + ok);
        File.WriteAllText(logPath, log.ToString());
        return ok ? 0 : 1;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(c switch
            {
                '' => "<ESC>",
                '\r' => "<CR>",
                '\n' => "<LF>",
                _ => c < 32 ? "<" + ((int)c).ToString("X2") + ">" : c.ToString(),
            });
        return sb.ToString();
    }

    private static string CommandLineFor(string shell) => shell switch
    {
        "pwsh" => "pwsh.exe -NoLogo -NoProfile",
        "powershell" => "powershell.exe -NoLogo -NoProfile",
        "cmd" => "cmd.exe",
        _ => shell,
    };

    private static bool RunShell(string shell)
    {
        Console.WriteLine("### " + shell);
        var col = new Collector();
        var spawnSw = Stopwatch.StartNew();

        using var pty = new PtySession(CommandLineFor(shell), 120, 30);
        pty.DataReceived += col.Feed;

        if (!col.WaitQuiet(400, 15000))
        {
            Console.WriteLine("  no output within 15s — is " + shell + " on PATH?");
            return false;
        }
        var timeToFirstByte = (col.FirstByteAt - (DateTime.UtcNow - spawnSw.Elapsed)).TotalMilliseconds;
        var timeToPrompt = spawnSw.Elapsed.TotalMilliseconds;

        Console.WriteLine("  spawn: pid " + pty.ProcessId +
                          "   first byte " + Math.Max(0, timeToFirstByte).ToString("F0") + "ms" +
                          "   prompt ready " + timeToPrompt.ToString("F0") + "ms" +
                          "   (" + col.TotalBytes + " bytes in " + col.Chunks + " chunks)");

        var okEcho = EchoLatency(pty, col, shell);
        var okThroughput = Throughput(pty, col, shell);
        var okResize = ResizeCheck(pty, col, shell);

        // Leave the shell cleanly.
        pty.Write("exit\r");
        pty.WaitForExit(3000);
        return okEcho && okThroughput && okResize;
    }

    /// <summary>The input-lag number. Type one character, wait for the shell to echo it.</summary>
    private static bool EchoLatency(PtySession pty, Collector col, string shell)
    {
        const int warmup = 5, iterations = 40;
        var samples = new List<double>();

        for (int i = 0; i < warmup + iterations; i++)
        {
            col.WaitQuiet(60, 2000);
            var from = col.Position;
            pty.Write("x");
            var ms = col.WaitForChar('x', from, 3000);
            if (ms < 0) { Console.WriteLine("  echo: TIMED OUT on iteration " + i); return false; }
            if (i >= warmup) samples.Add(ms);

            col.WaitQuiet(40, 1000);
            pty.Write("\b");   // backspace, keep the line clean
        }

        col.WaitQuiet(80, 2000);
        pty.Write("\u0003");   // Ctrl+C to abandon whatever is on the line
        col.WaitQuiet(150, 2000);

        Console.WriteLine("  " + Stats.Describe("echo latency (1 keystroke)", samples));
        var p95 = Stats.Percentile(samples, 95);
        if (p95 > 16.0)
            Console.WriteLine("      note: p95 " + p95.ToString("F1") + "ms exceeds one 60Hz frame (16.7ms)");
        return true;
    }

    /// <summary>Bulk output: the realistic worst case is dumping a large file into the terminal.</summary>
    private static bool Throughput(PtySession pty, Collector col, string shell)
    {
        var path = Path.Combine(Path.GetTempPath(), "winmux-spike1-payload.txt");
        const int lines = 60000;
        if (!File.Exists(path) || new FileInfo(path).Length < 1_000_000)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines; i++)
                sb.Append("line ").Append(i.ToString("D6"))
                  .Append(" ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789\r\n");
            File.WriteAllText(path, sb.ToString());
        }
        var payloadBytes = new FileInfo(path).Length;

        // Marker is assembled by the shell so the echoed command text does not contain it.
        string cmd = shell == "cmd"
            ? "type \"" + path + "\" & echo DONE%COMSPEC:~0,0%MARK\r"
            : "[Console]::Out.Write([IO.File]::ReadAllText('" + path + "')); ('DON'+'EMARK')\r";

        col.WaitQuiet(150, 3000);
        var from = col.Position;
        var bytesBefore = col.TotalBytes;
        var sw = Stopwatch.StartNew();
        pty.Write(cmd);

        if (!col.WaitFor("DONEMARK", from, 60000))
        {
            Console.WriteLine("  throughput: TIMED OUT waiting for completion marker");
            return false;
        }
        sw.Stop();
        var got = col.TotalBytes - bytesBefore;
        var mbps = got / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;

        Console.WriteLine("  throughput: payload " + (payloadBytes / 1024 / 1024.0).ToString("F1") + " MiB" +
                          " -> read " + (got / 1024 / 1024.0).ToString("F1") + " MiB" +
                          " in " + sw.Elapsed.TotalSeconds.ToString("F2") + "s" +
                          " = " + mbps.ToString("F1") + " MiB/s");
        return true;
    }

    /// <summary>Does the shell actually observe a ResizePseudoConsole?</summary>
    private static bool ResizeCheck(PtySession pty, Collector col, string shell)
    {
        (short cols, short rows)[] sizes = [(100, 30), (160, 50), (80, 24)];
        var allOk = true;

        foreach (var (cols, rows) in sizes)
        {
            col.WaitQuiet(150, 3000);
            pty.Resize(cols, rows);
            Thread.Sleep(120);

            var from = col.Position;
            string cmd = shell == "cmd"
                ? "mode con | findstr /C:\"Columns\"\r"
                : "('SIZ'+'E=') + [Console]::WindowWidth + 'x' + [Console]::WindowHeight\r";
            pty.Write(cmd);

            bool ok;
            if (shell == "cmd")
            {
                // `mode con` answers with "Columns:   100". Searching for the bare word "Columns"
                // would match the ECHO of the typed command line, so require the colon and digits.
                col.WaitFor("Columns:", from, 8000);
                var m = System.Text.RegularExpressions.Regex.Matches(col.TailSnapshot(), @"Columns:\s*(\d+)");
                var reported = m.Count > 0 ? int.Parse(m[^1].Groups[1].Value) : -1;
                ok = reported == cols;
                Console.WriteLine("  resize " + cols + "x" + rows + ": " +
                    (reported < 0 ? "NO ANSWER"
                     : ok ? "shell agrees (Columns: " + reported + ")"
                          : "MISMATCH — shell reports " + reported + " columns"));
            }
            else
            {
                var expect = "SIZE=" + cols + "x" + rows;
                ok = col.WaitFor(expect, from, 8000);
                Console.WriteLine("  resize " + cols + "x" + rows + ": " + (ok ? "shell agrees" : "MISMATCH — shell did not report " + expect));
            }
            allOk &= ok;
        }
        return allOk;
    }
}
