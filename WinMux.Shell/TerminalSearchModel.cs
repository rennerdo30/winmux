namespace WinMux.Shell;

/// <summary>One occurrence of a search term, in absolute row coordinates.</summary>
public readonly record struct TerminalMatch(int Row, int Column, int Length);

/// <summary>
/// Finding text in a terminal's scrollback.
///
/// The engine has retained five thousand rows since Phase 1, the viewport made them reachable, and
/// there was still no way to find anything in them — which is most of what scrollback is for. This
/// is the arithmetic: which rows contain the term, and which match comes next. Rows are absolute,
/// like everything else the viewport deals in, so a match stays on the same text when new output
/// arrives underneath it.
/// </summary>
public static class TerminalSearchModel
{
    /// <summary>
    /// Every occurrence, in reading order.
    ///
    /// Non-overlapping: searching "aa" in "aaaa" finds two matches, not three. Overlapping matches
    /// make "next" ambiguous and there is no reading of a terminal search where they are wanted.
    /// </summary>
    public static IReadOnlyList<TerminalMatch> Find(
        IReadOnlyList<string> rows,
        string? query,
        bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var matches = new List<TerminalMatch>();
        if (string.IsNullOrEmpty(query)) return matches;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        for (var row = 0; row < rows.Count; row++)
        {
            var text = rows[row];
            if (string.IsNullOrEmpty(text)) continue;

            var from = 0;
            while (from <= text.Length - query.Length)
            {
                var at = text.IndexOf(query, from, comparison);
                if (at < 0) break;
                matches.Add(new TerminalMatch(row, at, query.Length));
                from = at + query.Length;
            }
        }

        return matches;
    }

    /// <summary>
    /// The match to go to, wrapping at both ends.
    ///
    /// Wrapping rather than stopping, because a terminal search that refuses to continue past the
    /// last match makes people retype the query to start again.
    /// </summary>
    public static int Step(int current, int count, bool forward)
    {
        if (count <= 0) return -1;
        if (current < 0) return forward ? 0 : count - 1;
        return forward ? (current + 1) % count : (current - 1 + count) % count;
    }

    /// <summary>
    /// The match nearest the row currently on screen, so the first Enter goes somewhere sensible
    /// rather than to the top of a five-thousand-row history.
    /// </summary>
    public static int NearestTo(IReadOnlyList<TerminalMatch> matches, int row)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0) return -1;

        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < matches.Count; i++)
        {
            var distance = Math.Abs(matches[i].Row - row);
            if (distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>"3 of 17", or what to say when there is nothing.</summary>
    public static string Describe(int current, int count, bool hasQuery) => count switch
    {
        0 when !hasQuery => "",
        0 => "no matches",
        _ => $"{current + 1} of {count}",
    };
}
