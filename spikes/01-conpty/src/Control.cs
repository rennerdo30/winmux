using System.Text;

namespace ConPtySpike;

/// <summary>
/// Control experiment. Runs the same trivial "echo a marker" test through Porta.Pty, a
/// widely-used pty package, to separate "our interop is wrong" from "something environmental".
/// </summary>
internal static class Control
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("control: same test via Porta.Pty " + typeof(Porta.Pty.PtyOptions).Assembly.GetName().Version);

        var options = new Porta.Pty.PtyOptions
        {
            Name = "winmux-spike",
            Cols = 80,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            CommandLine = ["/c", "echo HELLO_PTY_MARKER"],
            Environment = new Dictionary<string, string>(),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var conn = await Porta.Pty.PtyProvider.SpawnAsync(options, cts.Token);
        Console.WriteLine("        spawned pid " + conn.Pid);

        var got = new StringBuilder();
        var done = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    int n = await conn.ReaderStream.ReadAsync(buf, cts.Token);
                    if (n <= 0) break;
                    var s = Encoding.UTF8.GetString(buf, 0, n);
                    lock (got) got.Append(s);
                    Console.WriteLine("        chunk " + n + " bytes: " + Escape(s));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally { done.TrySetResult(); }
        }, cts.Token);

        await Task.WhenAny(done.Task, Task.Delay(6000, CancellationToken.None));

        string all;
        lock (got) all = got.ToString();
        var ok = all.Contains("HELLO_PTY_MARKER", StringComparison.Ordinal);
        Console.WriteLine("        total " + all.Length + " chars");
        Console.WriteLine("        MARKER CAME THROUGH THE PIPE: " + ok);

        try { conn.Kill(); } catch { }
        try { conn.Dispose(); } catch { }
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
}
