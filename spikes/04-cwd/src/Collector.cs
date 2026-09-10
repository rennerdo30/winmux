using System.Text;

namespace CwdSpike;

/// <summary>
/// Accumulates pty output, decoding UTF-8 incrementally so multi-byte runs split across chunks survive.
///
/// Deliberately does NOT keep the whole stream. An earlier version appended everything to a
/// StringBuilder and re-materialised it on every chunk to search for markers, which is O(n^2) and
/// made a 4.3 MiB throughput run measure the harness rather than ConPTY. Only a bounded tail is
/// retained; markers always arrive at the end, and absolute character positions are tracked so a
/// stale earlier match cannot be mistaken for the one being waited on.
/// </summary>
internal sealed class Collector
{
    private const int TailChars = 1 << 16;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _tail = new();
    private readonly object _gate = new();
    private readonly char[] _chars = new char[64 * 1024];

    public long TotalBytes { get; private set; }
    public long TotalChars { get; private set; }
    public int Chunks { get; private set; }
    public DateTime FirstByteAt { get; private set; } = DateTime.MinValue;
    public DateTime LastByteAt { get; private set; } = DateTime.MinValue;

    /// <summary>Absolute character position one past the end of everything seen so far.</summary>
    public long Position { get { lock (_gate) return TotalChars; } }

    public void Feed(byte[] buf, int n)
    {
        lock (_gate)
        {
            if (FirstByteAt == DateTime.MinValue) FirstByteAt = DateTime.UtcNow;
            LastByteAt = DateTime.UtcNow;
            TotalBytes += n;
            Chunks++;

            int produced = _decoder.GetChars(buf, 0, n, _chars, 0, false);
            TotalChars += produced;
            _tail.Append(_chars, 0, produced);
            if (_tail.Length > TailChars * 2) _tail.Remove(0, _tail.Length - TailChars);

            Monitor.PulseAll(_gate);
        }
    }

    public string TailSnapshot() { lock (_gate) return _tail.ToString(); }

    /// <summary>Absolute position of <paramref name="marker"/>, or -1. Caller must hold the lock.</summary>
    private long FindLocked(string marker, long notBefore)
    {
        var tailStartAbs = TotalChars - _tail.Length;
        var s = _tail.ToString();
        int from = 0;
        while (true)
        {
            int idx = s.IndexOf(marker, from, StringComparison.Ordinal);
            if (idx < 0) return -1;
            var abs = tailStartAbs + idx;
            if (abs >= notBefore) return abs;
            from = idx + 1;
        }
    }

    /// <summary>Wait until <paramref name="marker"/> appears at or after absolute position <paramref name="notBefore"/>.</summary>
    public bool WaitFor(string marker, long notBefore, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (true)
            {
                if (FindLocked(marker, notBefore) >= 0) return true;
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return false;
                Monitor.Wait(_gate, Math.Min(remaining, 50));
            }
        }
    }

    /// <summary>
    /// Wait until <paramref name="marker"/> has appeared <paramref name="count"/> times at or after
    /// <paramref name="notBefore"/>. Every interactive shell echoes the command line it was given
    /// and then prints the result, so waiting for TWO occurrences means "the shell ran it", which
    /// a single occurrence does not.
    /// </summary>
    public bool WaitForOccurrences(string marker, long notBefore, int count, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (true)
            {
                if (CountLocked(marker, notBefore) >= count) return true;
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return false;
                Monitor.Wait(_gate, Math.Min(remaining, 50));
            }
        }
    }

    private int CountLocked(string marker, long notBefore)
    {
        var tailStartAbs = TotalChars - _tail.Length;
        var s = _tail.ToString();
        int from = 0, n = 0;
        while (true)
        {
            int idx = s.IndexOf(marker, from, StringComparison.Ordinal);
            if (idx < 0) return n;
            if (tailStartAbs + idx >= notBefore) n++;
            from = idx + 1;
        }
    }

    /// <summary>Wait until no byte has arrived for <paramref name="quietMs"/>. Used as "the prompt is ready".</summary>
    public bool WaitQuiet(int quietMs, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            DateTime last;
            lock (_gate) last = LastByteAt;
            if (last != DateTime.MinValue && (DateTime.UtcNow - last).TotalMilliseconds >= quietMs) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    /// <summary>
    /// Block until a character matching <paramref name="want"/> arrives at or after
    /// <paramref name="notBefore"/>. Returns latency in ms, or -1 on timeout.
    /// </summary>
    public double WaitForChar(char want, long notBefore, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (true)
            {
                if (FindLocked(want.ToString(), notBefore) >= 0) return sw.Elapsed.TotalMilliseconds;
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return -1;
                Monitor.Wait(_gate, Math.Min(remaining, 5));
            }
        }
    }
}

internal static class Stats
{
    public static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        var i = (int)Math.Ceiling(p / 100.0 * s.Count) - 1;
        return s[Math.Clamp(i, 0, s.Count - 1)];
    }

    public static string Describe(string label, List<double> xs) =>
        label.PadRight(26) +
        " n=" + xs.Count.ToString().PadLeft(3) +
        "  p50=" + Percentile(xs, 50).ToString("F2").PadLeft(7) + "ms" +
        "  p95=" + Percentile(xs, 95).ToString("F2").PadLeft(7) + "ms" +
        "  max=" + (xs.Count == 0 ? 0 : xs.Max()).ToString("F2").PadLeft(7) + "ms";
}
