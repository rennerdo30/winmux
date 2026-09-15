namespace WinMux.Shell;

/// <summary>A cell, addressed by its row in the engine's whole buffer rather than on screen.</summary>
/// <param name="Row">Absolute row index, counting scrollback from 0.</param>
/// <param name="Column">Column within that row.</param>
public readonly record struct TerminalPosition(int Row, int Column) : IComparable<TerminalPosition>
{
    public int CompareTo(TerminalPosition other) =>
        Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);

    public static bool operator <(TerminalPosition a, TerminalPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TerminalPosition a, TerminalPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TerminalPosition a, TerminalPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TerminalPosition a, TerminalPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// Which part of a terminal's buffer is on screen, and what is selected in it.
///
/// Split out of the control because it is the whole of the logic and none of the drawing: where
/// the view sits after a wheel notch, what happens to that view when output arrives underneath it,
/// and which cells a drag covers. All of it is arithmetic that was previously impossible to test
/// because it did not exist — the control drew the bottom of the buffer and nothing else.
///
/// **Rows are absolute throughout.** A selection made at row 400 stays on the same text when 50
/// more rows arrive, which is the entire reason for not using screen coordinates.
/// </summary>
internal sealed class TerminalViewport
{
    private int _lastTotalRows;

    /// <summary>How many rows above the live screen the view is parked. 0 means following output.</summary>
    public int ScrollOffset { get; private set; }

    /// <summary>Where a drag started, in absolute coordinates. Null when nothing is selected.</summary>
    public TerminalPosition? Anchor { get; private set; }

    /// <summary>Where the pointer is now. Equal to <see cref="Anchor"/> for a click with no drag.</summary>
    public TerminalPosition? Focus { get; private set; }

    public bool IsFollowing => ScrollOffset == 0;

    public bool HasSelection => Anchor is { } a && Focus is { } f && a != f;

    /// <summary>The selection in order, regardless of which way it was dragged.</summary>
    public (TerminalPosition Start, TerminalPosition End)? Selection =>
        Anchor is { } a && Focus is { } f && a != f
            ? a <= f ? (a, f) : (f, a)
            : null;

    /// <summary>The absolute row drawn at the top of the screen.</summary>
    public int TopRow(int totalRows, int screenRows) =>
        Math.Max(0, totalRows - screenRows - ScrollOffset);

    /// <summary>
    /// Move the view by <paramref name="rows"/>, positive to go back in history.
    /// </summary>
    /// <returns>True when the view actually moved, so the caller can skip a redraw.</returns>
    public bool Scroll(int rows, int scrollbackRows)
    {
        var next = Math.Clamp(ScrollOffset + rows, 0, Math.Max(0, scrollbackRows));
        if (next == ScrollOffset) return false;
        ScrollOffset = next;
        return true;
    }

    /// <summary>Jump back to the live screen, which is what typing should always do.</summary>
    public bool ScrollToBottom()
    {
        if (ScrollOffset == 0) return false;
        ScrollOffset = 0;
        return true;
    }

    public bool ScrollToTop(int scrollbackRows) => Scroll(int.MaxValue, scrollbackRows);

    /// <summary>
    /// Bring an absolute row into view, roughly a third from the top.
    ///
    /// A third rather than the very top, because a search match with no lines above it gives no
    /// context, and no context is most of why you searched.
    /// </summary>
    /// <returns>True when the view moved.</returns>
    public bool ScrollToRow(int row, int totalRows, int screenRows, int scrollbackRows)
    {
        if (screenRows <= 0) return false;

        var desiredTop = Math.Max(0, row - screenRows / 3);
        var offset = Math.Clamp(totalRows - screenRows - desiredTop, 0, Math.Max(0, scrollbackRows));
        if (offset == ScrollOffset) return false;
        ScrollOffset = offset;
        return true;
    }

    /// <summary>
    /// Account for output that has arrived since the last frame.
    ///
    /// While the user is reading history, new output must not drag the text upward under them:
    /// the offset grows by however many rows were added, which keeps the same absolute rows on
    /// screen. Following the output — the usual case — is left alone.
    /// </summary>
    public void OnBufferGrew(int totalRows, int scrollbackRows)
    {
        var added = totalRows - _lastTotalRows;
        _lastTotalRows = totalRows;
        if (added <= 0 || ScrollOffset == 0) return;
        ScrollOffset = Math.Clamp(ScrollOffset + added, 0, Math.Max(0, scrollbackRows));
    }

    public void BeginSelection(TerminalPosition at)
    {
        Anchor = at;
        Focus = at;
    }

    public void ExtendSelection(TerminalPosition to) => Focus = to;

    /// <summary>Select an exact range, for word and line selection.</summary>
    public void SetSelection(TerminalPosition from, TerminalPosition to)
    {
        Anchor = from;
        Focus = to;
    }

    public bool ClearSelection()
    {
        if (Anchor is null && Focus is null) return false;
        Anchor = null;
        Focus = null;
        return true;
    }

    /// <summary>
    /// The half-open column span selected on one absolute row, or null when the row has none.
    ///
    /// A multi-row selection covers a whole row between its first and last, which is what makes a
    /// dragged selection look like selected text rather than a diagonal ribbon.
    /// </summary>
    public (int Start, int End)? RowSpan(int row, int columns)
    {
        if (Selection is not { } selection) return null;
        var (start, end) = selection;
        if (row < start.Row || row > end.Row) return null;

        var from = row == start.Row ? start.Column : 0;
        var to = row == end.Row ? end.Column : columns;
        from = Math.Clamp(from, 0, columns);
        to = Math.Clamp(to, 0, columns);
        return to <= from ? null : (from, to);
    }
}
