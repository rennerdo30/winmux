using System.Text;
using Avalonia.Threading;

namespace WinMux.Shell;

/// <summary>
/// A record of how the process ended, in <c>%LOCALAPPDATA%\WinMux\crash.log</c>.
///
/// Added after WinMux vanished during a test with nothing in the Windows event log and nothing of
/// its own to say why — the one outcome CLAUDE.md section 8 forbids outright. Every unhandled
/// exception is written with its stack, from any thread, from the UI dispatcher and from unobserved
/// tasks; and an ordinary exit writes one line too, so that "it crashed" and "it was closed" can be
/// told apart after the fact.
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMux", "crash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write($"unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write($"process exit, code {Environment.ExitCode}", null);
    }

    /// <summary>The dispatcher's own hook, which exists only once Avalonia is running.</summary>
    public static void WatchDispatcher() =>
        Dispatcher.UIThread.UnhandledException += (_, e) => Write("unhandled exception on the UI thread", e.Exception);

    public static void Write(string what, Exception? exception)
    {
        try
        {
            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append("  ").Append(what)
                .Append("  pid ").Append(Environment.ProcessId)
                .Append("  version ").Append(typeof(CrashLog).Assembly.GetName().Version?.ToString(3))
                .AppendLine();
            if (exception is not null) entry.AppendLine(exception.ToString());

            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                // Kept small: a log nobody reads must not grow without end.
                if (File.Exists(Path) && new FileInfo(Path).Length > 1024 * 1024) File.Delete(Path);
                File.AppendAllText(Path, entry.ToString());
            }
        }
        catch (Exception)
        {
            // The log is the last resort; if it cannot be written there is nowhere left to say so.
        }
    }
}
