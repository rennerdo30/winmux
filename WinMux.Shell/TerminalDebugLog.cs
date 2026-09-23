using System.Text;

namespace WinMux.Shell;

/// <summary>
/// A record of what a terminal pane did with each key, for finding out why a key does not reach a
/// program the way it should.
///
/// Off unless WinMux is started with <c>WINMUX_DEBUG_KEYS=1</c> in its environment, because it
/// records what is typed — a password typed into a pane included. It writes to
/// <c>%LOCALAPPDATA%\WinMux\keys-debug.log</c>, on this machine only.
/// </summary>
internal static class TerminalDebugLog
{
    private static readonly object Gate = new();

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("WINMUX_DEBUG_KEYS") == "1";

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMux", "keys-debug.log");

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // A diagnostic that breaks the thing it is diagnosing is worse than none.
        }
    }

    /// <summary>Bytes as hex with a readable rendering, so ESC and control characters are visible.</summary>
    public static string Describe(ReadOnlySpan<byte> bytes)
    {
        var hex = new StringBuilder();
        var text = new StringBuilder();
        foreach (var value in bytes)
        {
            hex.Append(value.ToString("X2")).Append(' ');
            text.Append(value switch
            {
                0x1b => "<ESC>",
                < 0x20 => $"<^{(char)(value + 64)}>",
                0x7f => "<DEL>",
                _ => ((char)value).ToString(),
            });
        }

        return $"{hex.ToString().TrimEnd()}  \"{text}\"";
    }
}
