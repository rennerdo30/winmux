using System.Diagnostics;
using System.Text;
using Terminal.Emulation;

namespace ConPtySpike;

/// <summary>
/// Spike 1, stage B: the question that actually gates the stack — adopt a VT engine or write one?
/// Exercises Terminal.Emulation (the engine underneath Terminal.Avalonia) headlessly, so
/// correctness and throughput can be measured without standing up a GUI.
/// </summary>
internal static class StageB
{
    public static int Run()
    {
        Console.WriteLine("Spike 1 — stage B: VT engine evaluation (Terminal.Emulation "
                          + typeof(Terminal.Emulation.Terminal).Assembly.GetName().Version + ")");
        Console.WriteLine(new string('=', 78));

        var ok = true;
        ok &= B1_ParseThroughput();
        Console.WriteLine();
        ok &= B2_EndToEnd();
        Console.WriteLine();
        ok &= B3_ReflowOnResize();
        Console.WriteLine();
        ok &= B4_AlternateScreenAndResponses();
        return ok ? 0 : 1;
    }

    /// <summary>Raw parser speed, decoupled from ConPTY. This is the number that decides adopt-vs-write.</summary>
    private static bool B1_ParseThroughput()
    {
        Console.WriteLine("### B1 raw parse throughput");
        var path = Path.Combine(Path.GetTempPath(), "winmux-spike1-payload.txt");
        if (!File.Exists(path)) { Console.WriteLine("  payload missing — run stage A first"); return false; }
        var bytes = File.ReadAllBytes(path);

        foreach (var scrollback in new[] { 0, 10_000 })
        {
            var term = new Terminal.Emulation.Terminal(120, 30, scrollback);
            // Warm up the JIT before timing.
            term.Write(bytes.AsSpan(0, Math.Min(64 * 1024, bytes.Length)));

            var term2 = new Terminal.Emulation.Terminal(120, 30, scrollback);
            var sw = Stopwatch.StartNew();
            const int chunk = 16 * 1024;
            for (int off = 0; off < bytes.Length; off += chunk)
                term2.Write(bytes.AsSpan(off, Math.Min(chunk, bytes.Length - off)));
            sw.Stop();

            var mib = bytes.Length / 1024.0 / 1024.0;
            Console.WriteLine("  scrollback " + scrollback.ToString().PadLeft(6) + " lines: " +
                              mib.ToString("F1") + " MiB in " + sw.Elapsed.TotalSeconds.ToString("F3") + "s = " +
                              (mib / sw.Elapsed.TotalSeconds).ToString("F0").PadLeft(4) + " MiB/s" +
                              "   (scrolled " + term2.ScrollbackLinesPushed + " lines)");
        }
        return true;
    }

    /// <summary>Real shell, real ConPTY, real parsing. Verifies the grid actually contains what it should.</summary>
    private static bool B2_EndToEnd()
    {
        Console.WriteLine("### B2 end-to-end: pwsh -> ConPTY -> Terminal.Emulation");
        var term = new Terminal.Emulation.Terminal(120, 30, 5000);
        long parseTicks = 0;

        using var pty = new PtySession("pwsh.exe -NoLogo -NoProfile", 120, 30);
        // Queries like DA/DSR must be answered or shells stall waiting for a reply.
        var responses = 0;
        term.Response += (_, r) => { responses++; pty.WriteBytes(r.Span); };
        term.DataSink(pty, ref parseTicks);

        var col = new Collector();
        pty.DataReceived += col.Feed;

        if (!col.WaitQuiet(500, 15000)) { Console.WriteLine("  pwsh never settled"); return false; }

        var marker = "WINMUX_B2_OK";
        var from = col.Position;
        pty.Write("('WINMUX' + '_B2_OK')\r");
        if (!col.WaitFor(marker, from, 10000)) { Console.WriteLine("  marker never arrived"); return false; }
        col.WaitQuiet(400, 5000);

        var screen = RenderScreen(term);
        var hit = screen.Contains(marker, StringComparison.Ordinal);
        Console.WriteLine("  parsed " + col.TotalBytes + " bytes, " + responses + " responses written back");
        Console.WriteLine("  cursor at row " + term.CursorRow + " col " + term.CursorColumn +
                          ", title " + (string.IsNullOrEmpty(term.Title) ? "(none)" : "\"" + term.Title + "\""));
        Console.WriteLine("  screen contains marker: " + hit);
        Console.WriteLine("  --- last 6 non-empty rows as the engine sees them ---");
        foreach (var l in screen.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6))
            Console.WriteLine("  | " + l.TrimEnd());

        pty.Write("exit\r");
        pty.WaitForExit(3000);
        return hit;
    }

    /// <summary>Resize with reflow — the thing a pane multiplexer does constantly.</summary>
    private static bool B3_ReflowOnResize()
    {
        Console.WriteLine("### B3 resize with reflow");
        var term = new Terminal.Emulation.Terminal(40, 10, 1000);
        var sentence = "The quick brown fox jumps over the lazy dog and keeps running well past the margin.";
        term.Write(Encoding.UTF8.GetBytes(sentence));

        var before = RenderScreen(term).TrimEnd();
        term.Resize(20, 10, reflow: true);
        var afterNarrow = RenderScreen(term).TrimEnd();
        term.Resize(100, 10, reflow: true);
        var afterWide = RenderScreen(term).TrimEnd();

        string Flat(string s) => string.Concat(s.Split('\n').Select(l => l.TrimEnd()));
        var keptNarrow = Flat(afterNarrow).Replace(" ", "").Contains("quickbrownfox", StringComparison.Ordinal);
        var keptWide = Flat(afterWide).Replace(" ", "").Contains("pastthemargin", StringComparison.Ordinal);

        Console.WriteLine("  40 cols -> " + before.Split('\n').Count(l => l.Trim().Length > 0) + " non-empty rows");
        Console.WriteLine("  20 cols -> " + afterNarrow.Split('\n').Count(l => l.Trim().Length > 0) + " non-empty rows, text preserved: " + keptNarrow);
        Console.WriteLine("  100 cols -> " + afterWide.Split('\n').Count(l => l.Trim().Length > 0) + " non-empty rows, text preserved: " + keptWide);
        if (!keptWide) { Console.WriteLine("  reflowed 100-col view:"); foreach (var l in afterWide.Split('\n').Take(4)) Console.WriteLine("  | " + l); }
        return keptNarrow && keptWide;
    }

    /// <summary>Alternate screen and wide characters — the two things naive emulators get wrong.</summary>
    private static bool B4_AlternateScreenAndResponses()
    {
        Console.WriteLine("### B4 alternate screen, wide chars, hyperlinks");
        var term = new Terminal.Emulation.Terminal(40, 8, 100);

        term.Write(Encoding.UTF8.GetBytes("primary content here"));
        var wasAlt0 = term.UsingAlternate;
        term.Write(Encoding.UTF8.GetBytes("[?1049h"));      // enter alt screen
        var wasAlt1 = term.UsingAlternate;
        term.Write(Encoding.UTF8.GetBytes("alt screen content"));
        var altText = RenderScreen(term);
        term.Write(Encoding.UTF8.GetBytes("[?1049l"));      // leave alt screen
        var wasAlt2 = term.UsingAlternate;
        var restored = RenderScreen(term).Contains("primary content here", StringComparison.Ordinal);

        Console.WriteLine("  alt flag primary/alt/restored: " + wasAlt0 + "/" + wasAlt1 + "/" + wasAlt2);
        Console.WriteLine("  alt screen showed its own content: " + altText.Contains("alt screen content", StringComparison.Ordinal));
        Console.WriteLine("  primary content restored on exit: " + restored);

        // CJK + emoji occupy two cells; a naive engine corrupts the grid here.
        var t2 = new Terminal.Emulation.Terminal(20, 3, 0);
        t2.Write(Encoding.UTF8.GetBytes("ab你好cd"));      // ab<ni><hao>cd
        var line = t2.GetRow(t2.ScrollbackCount);
        var widths = string.Join("", Enumerable.Range(0, 8).Select(i =>
            line.Cells[i].IsWideLeading ? "W" : line.Cells[i].IsWideTrailing ? "-" : line.Cells[i].IsBlank ? "." : "n"));
        var wideOk = widths.StartsWith("nnW-W-nn", StringComparison.Ordinal);
        Console.WriteLine("  cell widths for 'ab你好cd': " + widths + "  (expect nnW-W-nn) -> " + wideOk);

        var t3 = new Terminal.Emulation.Terminal(40, 3, 0);
        t3.Write(Encoding.UTF8.GetBytes("]8;;https://example.comlink]8;;"));
        var linkId = t3.GetRow(t3.ScrollbackCount).Cells[0].Link;
        var linkOk = linkId != 0 && t3.GetHyperlink(linkId) == "https://example.com";
        Console.WriteLine("  OSC 8 hyperlink captured: " + linkOk + " (" + (linkId == 0 ? "none" : t3.GetHyperlink(linkId)) + ")");

        return wasAlt1 && !wasAlt2 && restored && wideOk && linkOk;
    }

    private static string RenderScreen(Terminal.Emulation.Terminal t)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < t.Rows; r++)
        {
            var line = t.GetRow(t.ScrollbackCount + r);
            var lb = new StringBuilder();
            for (int c = 0; c < line.Length; c++)
            {
                ref var cell = ref line.Cells[c];
                if (cell.IsWideTrailing) continue;
                if (cell.IsBlank) lb.Append(' ');
                else cell.AppendGlyph(lb);
            }
            sb.Append(lb.ToString().TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Pipe pty output into the emulator, timing only the parse.</summary>
    private static void DataSink(this Terminal.Emulation.Terminal term, PtySession pty, ref long ticks)
    {
        pty.DataReceived += (buf, n) =>
        {
            lock (term) term.Write(buf.AsSpan(0, n));
        };
    }
}
