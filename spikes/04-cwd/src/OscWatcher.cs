using System.Text;

namespace CwdSpike;

/// <summary>
/// Strategy 1 from CLAUDE.md section 4: the shell reports its own cwd via OSC 9;9 or OSC 7.
/// The only accurate method when the shell has a child process running.
///
/// Watches the raw pty byte stream for
///   OSC 9;9;&lt;path&gt;  ST      (Windows convention, absolute Windows path)
///   OSC 7;file://host/path ST  (xterm convention, percent-encoded URL)
/// terminated by BEL (0x07) or ST (ESC \).
/// </summary>
internal sealed class OscWatcher
{
    private enum State { Text, Esc, InOsc, OscEsc }

    private State _state = State.Text;
    private readonly StringBuilder _osc = new();
    private readonly object _gate = new();

    public string? LastOsc99 { get; private set; }
    public string? LastOsc7 { get; private set; }
    public int Osc99Count { get; private set; }
    public int Osc7Count { get; private set; }
    /// <summary>Every OSC sequence seen, so the spike can report what a shell actually emits.</summary>
    public readonly List<string> AllOsc = [];

    public void Feed(byte[] buf, int n)
    {
        lock (_gate)
        {
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                switch (_state)
                {
                    case State.Text:
                        if (b == 0x1B) _state = State.Esc;
                        break;

                    case State.Esc:
                        if (b == (byte)']') { _state = State.InOsc; _osc.Clear(); }
                        else _state = State.Text;
                        break;

                    case State.InOsc:
                        if (b == 0x07) { Complete(); }
                        else if (b == 0x1B) { _state = State.OscEsc; }
                        else if (_osc.Length < 8192) _osc.Append((char)b);
                        break;

                    case State.OscEsc:
                        // ESC \ terminates; anything else was an ESC inside the payload.
                        if (b == (byte)'\\') Complete();
                        else { _osc.Append('\x1b').Append((char)b); _state = State.InOsc; }
                        break;
                }
            }
        }
    }

    private void Complete()
    {
        // The payload was accumulated byte-wise; re-decode as UTF-8 so non-ASCII paths survive.
        var raw = _osc.ToString();
        var bytes = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++) bytes[i] = (byte)raw[i];
        var s = Encoding.UTF8.GetString(bytes);

        AllOsc.Add(s.Length > 200 ? s[..200] + "…" : s);

        if (s.StartsWith("9;9;", StringComparison.Ordinal))
        {
            LastOsc99 = Normalize(s[4..].Trim().Trim('"'));
            Osc99Count++;
        }
        else if (s.StartsWith("7;", StringComparison.Ordinal))
        {
            LastOsc7 = ParseFileUrl(s[2..].Trim());
            Osc7Count++;
        }

        _osc.Clear();
        _state = State.Text;
    }

    /// <summary>OSC 7 carries a file:// URL with percent-encoding and a host component.</summary>
    public static string? ParseFileUrl(string url)
    {
        if (url.Length == 0) return null;
        if (!url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return Normalize(url);
        var rest = url[7..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var path = rest[slash..];
        try { path = Uri.UnescapeDataString(path); } catch { }
        // A Windows path arrives as /C:/foo; a Linux one as /home/foo and stays as-is.
        if (path.Length > 2 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':') path = path[1..];
        return Normalize(path);
    }

    public static string Normalize(string p)
    {
        p = p.Trim().Trim('"');
        if (p.Length > 1) p = p.TrimEnd('\\', '/');
        return p;
    }

    /// <summary>
    /// Comparison form — used ONLY to decide hit/miss, never for display. An earlier version
    /// folded separators inside Normalize as well, which printed Linux paths as \tmp\foo.
    /// </summary>
    public static string Canonical(string p) => Normalize(p).Replace('\\', '/');

    public void Reset()
    {
        lock (_gate)
        {
            LastOsc99 = LastOsc7 = null;
            Osc99Count = Osc7Count = 0;
            AllOsc.Clear();
        }
    }
}
