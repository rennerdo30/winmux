using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using WinMux.Pty;
using WinMux.Terminal;

namespace WinMux.Shell;

/// <summary>A small Avalonia terminal surface backed by WinMux's owned PTY and VT seams.</summary>
internal sealed class TerminalPaneControl : Control, IDisposable
{
    /// <summary>
    /// Cell geometry, measured from the font in use rather than assumed.
    ///
    /// Per instance and re-measured when the font setting changes; see
    /// <see cref="TerminalFontMetrics"/> for what the assumed numbers cost.
    /// </summary>
    private TerminalFontMetrics _metrics;
    private Typeface _regular;
    private Typeface _bold;
    private Typeface _italic;
    private Typeface _boldItalic;

    private double FontSize => _metrics.FontSize;
    private double CellWidth => _metrics.CellWidth;
    private double CellHeight => _metrics.CellHeight;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(TerminalPalette.DefaultBackground);
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xCC, 0xCC, 0xCC));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x78, 0xFF));
    private static readonly IBrush ScrollbarBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xCC, 0xCC, 0xCC));

    /// <summary>Every match, and the one the user is standing on. Distinct, or "next" means nothing.</summary>
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xC1, 0x9C, 0x00));
    private static readonly IBrush CurrentMatchBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0xF9, 0xF1, 0xA5));

    /// <summary>
    /// Space between the pane's edge and its text, in pixels.
    ///
    /// Windows Terminal's default, and the loudest single tell that something is a raw control:
    /// text starting at x=0 touches the window edge in a way no shipped application's does. It is
    /// taken out of the usable area, so the column and row counts are computed from the inset
    /// bounds and every draw and hit test is offset by it.
    /// </summary>
    private const double Inset = 8;

    /// <summary>The pane's own corner rounding. Windows 11 rounds surfaces at 8.</summary>
    private const double CornerRadius = 8;

    /// <summary>Brushes for colours the VT stream asks for, kept so a rainbow prompt is not an allocation storm.</summary>
    private static readonly Dictionary<uint, IBrush> BrushCache = [];

    /// <summary>Rows per wheel notch. Three is what every terminal on this machine uses.</summary>
    private const int WheelRows = 3;

    private const double ScrollbarWidth = 4;

    private readonly object _gate = new();
    private readonly string _program;
    private readonly string[] _arguments;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string> _environment;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITerminalEngine _engine;

    private readonly TerminalViewport _viewport = new();
    private bool _dragging;

    /// <summary>The link under the pointer while Ctrl is held, and the absolute row it sits on.</summary>
    private (TerminalLink Link, int Row)? _hoveredLink;

    /// <summary>One hand, not one per pointer move. A Cursor owns a native handle.</summary>
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private IReadOnlyList<TerminalMatch> _matches = [];
    private int _currentMatch = -1;
    private string _searchQuery = "";

    private IPtySession? _session;
    private Task? _startTask;
    private int _disposed;
    private int _resizeQueued;

    public TerminalPaneControl(
        string program,
        IEnumerable<string>? arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        _program = program;
        _arguments = arguments?.ToArray() ?? [];
        _workingDirectory = workingDirectory;
        _environment = environment is null
            ? []
            : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        _engine = new TerminalEmulationEngine(columns: 120, rows: 30);
        _engine.Updated += OnEngineUpdated;
        _engine.TitleChanged += OnEngineTitleChanged;
        _engine.WorkingDirectoryChanged += OnEngineWorkingDirectoryChanged;
        _engine.Response += OnEngineResponse;
        _engine.NotificationRequested += OnEngineNotification;
        _engine.ClipboardWriteRequested += OnEngineClipboardWrite;

        Focusable = true;
        ClipToBounds = true;

        ApplyFont(Settings.ShellSettings.Current.TerminalFontFamily,
                  Settings.ShellSettings.Current.TerminalFontSize);
        Settings.ShellSettings.Changed += OnSettingsChanged;
    }

    public event Action<string>? TitleChanged;

    public event Action<string>? WorkingDirectoryChanged;

    public event Action<int>? Exited;

    /// <summary>
    /// The program asked for attention (OSC 9/777/99 or a bell). Raised on the UI thread; whether it
    /// becomes a Windows notification is the window's decision, not the pane's.
    /// </summary>
    public event Action<TerminalNotification>? NotificationRequested;

    public int? ProcessId
    {
        get
        {
            lock (_gate) return _session?.ProcessId;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            return _startTask ??= StartCoreAsync(cancellationToken);
        }
    }

    public ValueTask SendText(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Rounded, so the pane reads as a tile rather than as the whole window being black. The
        // corners let the canvas behind show through, which is the same thing the divider gutters
        // already rely on.
        var renderBounds = new Rect(Bounds.Size);
        context.DrawRectangle(BackgroundBrush, null, new RoundedRect(renderBounds, CornerRadius));

        // The cursor belongs to the live screen. Drawing it while the user is reading history
        // would put it on an unrelated row and imply typing would land there.
        var cursor = _engine.Cursor;
        if (_viewport.IsFollowing && cursor.Visible && cursor.Row >= 0 && cursor.Row < _engine.Rows)
        {
            context.FillRectangle(
                CursorBrush,
                new Rect(Inset + cursor.Column * CellWidth, Inset + cursor.Row * CellHeight, CellWidth, CellHeight),
                0);
        }

        var columns = _engine.Columns;
        var visibleRows = Math.Min(_engine.Rows, _metrics.RowsIn(Bounds.Height, Inset));
        var firstRow = _viewport.TopRow(_engine.TotalRows, _engine.Rows);

        var cells = new TerminalCell[columns];

        for (var row = 0; row < visibleRows; row++)
        {
            Array.Clear(cells);
            var info = _engine.CopyRow(firstRow + row, cells);
            var y = Inset + row * CellHeight;

            // Cell backgrounds and the glyphs on top of them, run by run. One FormattedText for
            // the whole row is what discarded every colour the engine had already parsed.
            foreach (var run in TerminalRunSplitter.Split(cells, Math.Min(info.Length, columns)))
            {
                var inverse = (run.Attributes & TerminalCellAttributes.Inverse) != 0;
                var foreground = TerminalPalette.Resolve(inverse ? run.Background : run.Foreground, inverse);
                var background = TerminalPalette.Resolve(inverse ? run.Foreground : run.Background, !inverse);

                var x = Inset + run.Column * CellWidth;
                var width = run.Columns * CellWidth;

                if (background != TerminalPalette.DefaultBackground)
                    context.FillRectangle(BrushFor(background), new Rect(x, y, width, CellHeight), 0);

                if ((run.Attributes & TerminalCellAttributes.Invisible) != 0) continue;
                if ((run.Attributes & TerminalCellAttributes.Faint) != 0)
                    foreground = TerminalPalette.Faint(foreground, background);

                var text = new FormattedText(
                    run.Text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    TypefaceFor(run.Attributes),
                    FontSize,
                    BrushFor(foreground));
                context.DrawText(text, new Point(x, y));

                DrawDecorations(context, run, foreground, x, y, width);
            }
        }

        // Matches, under the selection and over the text, so both stay readable.
        for (var i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];
            var screenRow = match.Row - firstRow;
            if (screenRow < 0 || screenRow >= visibleRows) continue;

            context.FillRectangle(
                i == _currentMatch ? CurrentMatchBrush : MatchBrush,
                new Rect(Inset + match.Column * CellWidth, Inset + screenRow * CellHeight,
                    match.Length * CellWidth, CellHeight),
                2);
        }

        // Selection goes on top, translucent, so the text stays readable through it. It was drawn
        // underneath when every glyph was opaque and the background never was.
        for (var row = 0; row < visibleRows; row++)
        {
            if (_viewport.RowSpan(firstRow + row, columns) is not { } span) continue;
            context.FillRectangle(
                SelectionBrush,
                new Rect(Inset + span.Start * CellWidth, Inset + row * CellHeight,
                    (span.End - span.Start) * CellWidth, CellHeight),
                0);
        }

        // The hovered link, underlined in the accent. A hand cursor alone says *something* here is
        // clickable; the underline says which characters, which matters when two URLs sit on one
        // line or a URL runs into the prose after it.
        if (_hoveredLink is { } hovered)
        {
            var screenRow = hovered.Row - firstRow;
            if (screenRow >= 0 && screenRow < visibleRows)
            {
                var y = Inset + (screenRow + 1) * CellHeight - 1.5;
                context.DrawLine(
                    new Pen(Chrome.Palette.AccentBrush, 1),
                    new Point(Inset + hovered.Link.Start * CellWidth, y),
                    new Point(Inset + hovered.Link.End * CellWidth, y));
            }
        }

        DrawScrollbar(context, visibleRows);
    }

    /// <summary>Underlines, strikethrough and overline, which are lines rather than font choices.</summary>
    private void DrawDecorations(
        DrawingContext context,
        TerminalRun run,
        Color color,
        double x,
        double y,
        double width)
    {
        const TerminalCellAttributes AnyUnderline =
            TerminalCellAttributes.Underline |
            TerminalCellAttributes.DoubleUnderline |
            TerminalCellAttributes.CurlyUnderline;

        if ((run.Attributes & (AnyUnderline | TerminalCellAttributes.Strikethrough |
                               TerminalCellAttributes.Overline)) == 0)
        {
            return;
        }

        var pen = new Pen(BrushFor(color));

        if ((run.Attributes & AnyUnderline) != 0)
        {
            var baseline = y + CellHeight - 2.5;
            context.DrawLine(pen, new Point(x, baseline), new Point(x + width, baseline));

            // A curly underline is what a compiler uses to mean "wrong"; approximating it with a
            // second straight line would say something different.
            if ((run.Attributes & TerminalCellAttributes.DoubleUnderline) != 0)
                context.DrawLine(pen, new Point(x, baseline + 2), new Point(x + width, baseline + 2));
        }

        if ((run.Attributes & TerminalCellAttributes.Strikethrough) != 0)
        {
            var middle = y + CellHeight / 2;
            context.DrawLine(pen, new Point(x, middle), new Point(x + width, middle));
        }

        if ((run.Attributes & TerminalCellAttributes.Overline) != 0)
            context.DrawLine(pen, new Point(x, y + 0.5), new Point(x + width, y + 0.5));
    }

    /// <summary>
    /// A thin indicator on the right, drawn only once there is history to be in.
    ///
    /// Without it, scrolling back looks like the terminal has simply stopped updating: there is no
    /// other cue that the view has left the live screen.
    /// </summary>
    private void DrawScrollbar(DrawingContext context, int visibleRows)
    {
        var total = _engine.TotalRows;
        if (total <= visibleRows || visibleRows <= 0) return;

        var height = Bounds.Height - 2 * Inset;
        var thumbHeight = Math.Max(24, height * visibleRows / total);
        var travel = height - thumbHeight;
        var top = _viewport.TopRow(total, _engine.Rows);
        var maxTop = Math.Max(1, total - visibleRows);
        var y = travel * top / maxTop;

        context.FillRectangle(
            ScrollbarBrush,
            new Rect(Bounds.Width - Inset - ScrollbarWidth, Inset + y, ScrollbarWidth, thumbHeight),
            (float)(ScrollbarWidth / 2));
    }

    /// <summary>
    /// A brush for a colour, reused.
    ///
    /// Render runs on the UI thread only, so no lock is needed; the cache is bounded in practice
    /// by how many distinct colours a terminal actually emits.
    /// </summary>
    private static IBrush BrushFor(Color color)
    {
        var key = (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
        if (BrushCache.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(color);
        BrushCache[key] = brush;
        return brush;
    }

    private Typeface TypefaceFor(TerminalCellAttributes attributes)
    {
        var bold = (attributes & TerminalCellAttributes.Bold) != 0;
        var italic = (attributes & TerminalCellAttributes.Italic) != 0;
        return (bold, italic) switch
        {
            (true, true) => _boldItalic,
            (true, false) => _bold,
            (false, true) => _italic,
            _ => _regular,
        };
    }

    /// <summary>
    /// Measure the configured font and rebuild the typefaces from it.
    ///
    /// Called on construction and whenever the setting changes, so a font change takes effect in
    /// every open pane without a restart — and so the grid is never drawn against numbers that
    /// describe a different font from the one the glyphs are in.
    /// </summary>
    private void ApplyFont(string? family, double fontSize)
    {
        _metrics = TerminalFontMetrics.Measure(family, fontSize);

        var resolved = _metrics.Typeface.FontFamily;
        _regular = _metrics.Typeface;
        _bold = new Typeface(resolved, FontStyle.Normal, FontWeight.Bold);
        _italic = new Typeface(resolved, FontStyle.Italic);
        _boldItalic = new Typeface(resolved, FontStyle.Italic, FontWeight.Bold);

        // The grid size in cells depends on the cell size, so a font change is a resize.
        ResizeFromBounds();
        InvalidateVisual();
    }

    /// <summary>How many matches the current query has.</summary>
    public int MatchCount => _matches.Count;

    /// <summary>Which match is current, or -1. Together with <see cref="MatchCount"/>, "3 of 17".</summary>
    public int CurrentMatch => _currentMatch;

    /// <summary>
    /// Search the whole buffer, scrollback included, and go to the match nearest the view.
    /// </summary>
    /// <returns>True if anything matched.</returns>
    public bool Search(string? query)
    {
        _searchQuery = query ?? "";
        _matches = TerminalSearchModel.Find(BufferText(), _searchQuery);
        _currentMatch = TerminalSearchModel.NearestTo(_matches, _viewport.TopRow(_engine.TotalRows, _engine.Rows));
        GoToCurrentMatch();
        return _matches.Count > 0;
    }

    /// <summary>Step to the next or previous match, wrapping.</summary>
    public void StepMatch(bool forward)
    {
        if (_matches.Count == 0) return;
        _currentMatch = TerminalSearchModel.Step(_currentMatch, _matches.Count, forward);
        GoToCurrentMatch();
    }

    /// <summary>Drop the search and its highlights, and return to the live screen.</summary>
    public void ClearSearch()
    {
        _searchQuery = "";
        _matches = [];
        _currentMatch = -1;
        _viewport.ScrollToBottom();
        InvalidateVisual();
    }

    private void GoToCurrentMatch()
    {
        if (_currentMatch >= 0 && _currentMatch < _matches.Count)
        {
            _viewport.ScrollToRow(
                _matches[_currentMatch].Row, _engine.TotalRows, _engine.Rows, _engine.ScrollbackCount);
        }

        InvalidateVisual();
    }

    /// <summary>
    /// The whole buffer as plain text, one string per absolute row.
    ///
    /// Built per search rather than kept: the engine is the buffer, and a second copy maintained
    /// alongside it would be a cache to invalidate on every write.
    /// </summary>
    private IReadOnlyList<string> BufferText()
    {
        var total = _engine.TotalRows;
        var columns = _engine.Columns;
        var cells = new TerminalCell[columns];
        var rows = new List<string>(total);
        var line = new StringBuilder(columns);

        for (var row = 0; row < total; row++)
        {
            Array.Clear(cells);
            var info = _engine.CopyRow(row, cells);
            line.Clear();
            for (var column = 0; column < Math.Min(info.Length, columns); column++)
            {
                if (cells[column].IsWideTrailing) continue;
                if (cells[column].IsBlank || cells[column].Character == '\0') line.Append(' ');
                else cells[column].AppendGlyph(line);
            }

            rows.Add(line.ToString());
        }

        return rows;
    }

    /// <summary>The absolute cell under a point, clamped so a drag outside the control still works.</summary>
    private TerminalPosition PositionAt(Point point)
    {
        var columns = Math.Max(1, _engine.Columns);
        var column = _metrics.ColumnAt(point.X, Inset, columns);
        var row = _viewport.TopRow(_engine.TotalRows, _engine.Rows) +
                  _metrics.RowAt(point.Y, Inset, _engine.Rows);
        return new TerminalPosition(Math.Clamp(row, 0, Math.Max(0, _engine.TotalRows - 1)), column);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_engine.ScrollbackCount <= 0) return;

        if (_viewport.Scroll((int)(e.Delta.Y * WheelRows), _engine.ScrollbackCount))
        {
            InvalidateVisual();
        }
        // Handled either way: a terminal that let the wheel bubble would scroll the pane container
        // when it reached the end of its own history.
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging)
        {
            // Only while Ctrl is held, which is the terminal convention and what keeps an ordinary
            // drag over a URL a selection rather than a misfired navigation.
            TrackLink(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? e.GetPosition(this) : null);
            return;
        }

        TrackLink(null);
        _viewport.ExtendSelection(PositionAt(e.GetPosition(this)));
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Remember the link under <paramref name="point"/>, underline it and offer a hand.
    ///
    /// Null clears it, which is what leaving the control, starting a drag, or letting go of Ctrl
    /// all mean: a pane still showing a hand over text that will not open is worse than one that
    /// never offered.
    /// </summary>
    private void TrackLink(Point? point)
    {
        var found = point is { } p ? LinkAt(PositionAt(p)) : null;
        if (found?.Link.Url == _hoveredLink?.Link.Url && found?.Row == _hoveredLink?.Row) return;

        _hoveredLink = found;
        Cursor = found is null ? null : HandCursor;
        ToolTip.SetTip(this, found?.Link.Url);
        InvalidateVisual();
    }

    /// <summary>The link at an absolute position, whether marked with OSC 8 or written in the text.</summary>
    private (TerminalLink Link, int Row)? LinkAt(TerminalPosition at)
    {
        var columns = _engine.Columns;
        if (columns <= 0) return null;

        var cells = new TerminalCell[columns];
        var info = _engine.CopyRow(at.Row, cells);
        if (info.Length == 0) return null;

        return TerminalLinkModel.At(cells.AsSpan(0, Math.Min(info.Length, columns)), at.Column, _engine.GetHyperlink)
            is { } link
            ? (link, at.Row)
            : null;
    }

    /// <summary>
    /// Hand a link to the system browser.
    ///
    /// <c>UseShellExecute</c> is what makes the string a request to the shell rather than a program
    /// to run — and it is also why <see cref="TerminalLinkModel.IsOpenable"/> is checked again here
    /// rather than trusted from the caller. The text came out of whatever is running in the pane.
    /// </summary>
    private void OpenLink(string url)
    {
        if (!TerminalLinkModel.IsOpenable(url)) return;

        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // A browser that will not start is not worth taking the pane down for.
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        TrackLink(null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        TerminalDebugLog.Write($"PointerReleased dragging={_dragging} selection={_viewport.HasSelection}");
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// Select the word under a position, the way a double-click does everywhere else.
    ///
    /// Path separators count as word characters, because the thing a person double-clicks in a
    /// terminal is nearly always a path or a file name.
    /// </summary>
    private void SelectWordAt(TerminalPosition at)
    {
        var columns = _engine.Columns;
        var cells = new TerminalCell[columns];
        var info = _engine.CopyRow(at.Row, cells);
        var length = Math.Min(info.Length, columns);
        if (length == 0 || at.Column >= length) return;

        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '\\' or ':' or '~';

        var start = at.Column;
        var end = at.Column;
        while (start > 0 && IsWord(Glyph(cells[start - 1]))) start--;
        while (end < length && IsWord(Glyph(cells[end]))) end++;
        if (end <= start) return;

        _viewport.SetSelection(new TerminalPosition(at.Row, start), new TerminalPosition(at.Row, end));
    }

    private void SelectLineAt(TerminalPosition at) => _viewport.SetSelection(
        new TerminalPosition(at.Row, 0),
        new TerminalPosition(at.Row, _engine.Columns));

    private static char Glyph(TerminalCell cell) =>
        cell.IsBlank || cell.Character == '\0' ? ' ' : cell.Character;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        TerminalDebugLog.Write($"PointerPressed clicks={e.ClickCount} handledAlready={e.Handled}");
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        var at = PositionAt(point.Position);

        // Ctrl+click opens; a plain click never does. Output is not a web page, and a terminal
        // where clicking a word could launch something would be a terminal nobody could click in.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkAt(at) is { } target)
        {
            OpenLink(target.Link.Url);
            e.Handled = true;
            return;
        }
        switch (e.ClickCount)
        {
            case 2:
                SelectWordAt(at);
                break;
            case >= 3:
                SelectLineAt(at);
                break;
            default:
                _viewport.BeginSelection(at);
                _dragging = true;
                e.Pointer.Capture(this);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Text the key handler has already sent, to be ignored if it also arrives as text input.</summary>
    private string? _suppressedText;

    protected override void OnTextInput(TextInputEventArgs e)
    {
        TerminalDebugLog.Write($"TextInput text={Show(e.Text)} suppressed={Show(_suppressedText)}");
        base.OnTextInput(e);
        if (_suppressedText is not null && e.Text == _suppressedText)
        {
            _suppressedText = null;
            e.Handled = true;
            return;
        }

        if (!string.IsNullOrEmpty(e.Text))
        {
            _ = WriteInputAsync(Encoding.UTF8.GetBytes(e.Text));
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (TerminalDebugLog.Enabled)
        {
            TerminalDebugLog.Write(
                $"KeyDown key={e.Key} modifiers={e.KeyModifiers} symbol={Show(e.KeySymbol)} physical={e.PhysicalKey} " +
                $"handledAlready={e.Handled} selection={_viewport.HasSelection} focusReporting={_engine.FocusReportingEnabled} " +
                $"bracketedPaste={_engine.BracketedPasteEnabled}");
        }

        base.OnKeyDown(e);

        _suppressedText = null;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.C)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }

        // Plain Ctrl+C copies when something is selected and interrupts when nothing is — Windows
        // Terminal's rule, and the one a Windows user's hands already follow. Sending the interrupt
        // regardless meant selecting Claude Code's answer and pressing Ctrl+C cancelled Claude.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.C && _viewport.HasSelection)
        {
            TerminalDebugLog.Write("  -> Ctrl+C with a selection: copying");
            _ = CopySelectionAsync();
            _viewport.ClearSelection();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Plain Ctrl+V pastes text. When the clipboard holds no text — an image, say — the key goes
        // through as Ctrl+V, so a program that reads the clipboard itself (Claude Code pasting a
        // screenshot) still can.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V)
        {
            TerminalDebugLog.Write("  -> Ctrl+V: paste text, or pass ^V through");
            _ = PasteOrPassThroughAsync();
            e.Handled = true;
            return;
        }

        // Shift+PageUp/Down and Ctrl+Shift+Home/End move the view. Unshifted PageUp belongs to the
        // program in the pane — less and vim both use it — so it is never intercepted.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key is Key.PageUp or Key.PageDown)
        {
            var page = Math.Max(1, _engine.Rows - 1);
            if (_viewport.Scroll(e.Key == Key.PageUp ? page : -page, _engine.ScrollbackCount))
                InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            e.Key is Key.Home or Key.End)
        {
            var moved = e.Key == Key.Home
                ? _viewport.ScrollToTop(_engine.ScrollbackCount)
                : _viewport.ScrollToBottom();
            if (moved) InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewport.HasSelection)
        {
            _viewport.ClearSelection();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if ((e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
             e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.V) ||
            (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Insert))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        var encoded = TerminalInput.EncodeKey(e.Key, e.KeyModifiers, e.KeySymbol, _engine.ApplicationCursorKeysEnabled);
        ReadOnlyMemory<byte> bytes = encoded ?? default;

        // An Alt combination was sent as ESC + character here; if the platform also reports the
        // character as text input, it must not arrive a second time.
        if (encoded is not null && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) _suppressedText = e.KeySymbol;

        if (TerminalDebugLog.Enabled)
        {
            TerminalDebugLog.Write(encoded is null
                ? "  -> not encoded here (left to text input, or not a key WinMux sends)"
                : "  -> encoded " + TerminalDebugLog.Describe(encoded));
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.V) _ = LogClipboardAsync("Alt+V");
        }

        if (!bytes.IsEmpty)
        {
            _ = WriteInputAsync(bytes);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsKeyboardFocusWithinProperty) ReportFocus();

        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && Interlocked.Exchange(ref _resizeQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(ResizeFromBounds, DispatcherPriority.Background);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Settings.ShellSettings.Changed -= OnSettingsChanged;
        _engine.Updated -= OnEngineUpdated;
        _engine.TitleChanged -= OnEngineTitleChanged;
        _engine.WorkingDirectoryChanged -= OnEngineWorkingDirectoryChanged;
        _engine.Response -= OnEngineResponse;
        _engine.NotificationRequested -= OnEngineNotification;
        _engine.ClipboardWriteRequested -= OnEngineClipboardWrite;
        _lifetime.Cancel();
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }

        _lifetime.Dispose();
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var session = await PtySession.StartAsync(
            new PtySessionOptions
            {
                Application = _program,
                Arguments = _arguments,
                WorkingDirectory = _workingDirectory,
                Environment = _environment,
                Columns = _engine.Columns,
                Rows = _engine.Rows,
            },
            startup.Token).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed != 0)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(TerminalPaneControl));
            }

            _session = session;
        }

        _ = PumpOutputAsync(session, _lifetime.Token);
        _ = ObserveExitAsync(session);
    }

    /// <summary>
    /// With <c>WINMUX_DEBUG_PTY=1</c>, every byte the program sends is saved, and every resize noted
    /// against the byte offset it happened at — enough to replay a screen that drew wrong.
    /// </summary>
    private FileStream? _capture;
    private StreamWriter? _captureEvents;
    private long _captured;

    private void OpenCapture()
    {
        if (Environment.GetEnvironmentVariable("WINMUX_DEBUG_PTY") != "1") return;
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMux", "pty-capture");
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40]);
            _capture = new FileStream(stem + ".bin", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _captureEvents = new StreamWriter(stem + ".events.log") { AutoFlush = true };
            _captureEvents.WriteLine($"program {_program} {string.Join(' ', _arguments)}");
            _captureEvents.WriteLine($"offset 0 size {_engine.Columns}x{_engine.Rows}");
        }
        catch (Exception)
        {
            _capture = null;
        }
    }

    private void CaptureEvent(string text)
    {
        try { _captureEvents?.WriteLine($"offset {Interlocked.Read(ref _captured)} {text}"); }
        catch (Exception) { }
    }

    private async Task PumpOutputAsync(IPtySession session, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        OpenCapture();
        try
        {
            while (true)
            {
                var read = await session.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                if (_capture is not null)
                {
                    try
                    {
                        _capture.Write(buffer, 0, read);
                        _capture.Flush();
                        Interlocked.Add(ref _captured, read);
                    }
                    catch (Exception) { }
                }

                _engine.Write(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private async Task ObserveExitAsync(IPtySession session)
    {
        try
        {
            var exitCode = await session.Exited.ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => Exited?.Invoke(exitCode));
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private void ResizeFromBounds()
    {
        Interlocked.Exchange(ref _resizeQueued, 0);
        if (_disposed != 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var columns = Math.Max(2, _metrics.ColumnsIn(Bounds.Width, Inset));
        var rows = _metrics.RowsIn(Bounds.Height, Inset);
        if (columns == _engine.Columns && rows == _engine.Rows)
        {
            return;
        }

        // Reflow on resize, not just on a user drag.
        //
        // The engine is constructed at a guessed 120x30 because the provider starts the pane before
        // the view is in the tree and there is no size to ask for yet. The first real layout then
        // grows the grid, and growing it without reflow left the content anchored to the bottom —
        // which is why every terminal opened with its prompt a third of the way down the pane and
        // blank space above it.
        CaptureEvent($"resize {columns}x{rows} (engine reflow: true)");
        _engine.Resize(columns, rows, reflow: true);
        lock (_gate)
        {
            _session?.Resize(columns, rows);
        }

        InvalidateVisual();
    }

    private void OnSettingsChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed != 0) return;
        ApplyFont(Settings.ShellSettings.Current.TerminalFontFamily,
                  Settings.ShellSettings.Current.TerminalFontSize);
    });

    private void OnEngineUpdated()
    {
        if (_disposed != 0) return;

        Dispatcher.UIThread.Post(
            () =>
            {
                // Told about growth before redrawing: while the user is reading history, new
                // output must not drag the text upward under them.
                _viewport.OnBufferChanged(_engine.TotalRows, _engine.ScrollbackCount);
                InvalidateVisual();
            },
            DispatcherPriority.Render);
    }

    private void OnEngineTitleChanged(string title)
    {
        if (_disposed == 0)
        {
            Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
        }
    }

    private void OnEngineWorkingDirectoryChanged(string path)
    {
        if (_disposed == 0)
        {
            Dispatcher.UIThread.Post(() => WorkingDirectoryChanged?.Invoke(path));
        }
    }

    private void OnEngineResponse(ReadOnlyMemory<byte> bytes)
    {
        if (_disposed == 0)
        {
            _ = WriteInputAsync(bytes.ToArray());
        }
    }

    private ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _session?.WriteAsync(bytes, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Anything the user sends returns the view to the bottom and drops the selection.
    ///
    /// Typing while parked in history and seeing nothing happen is the single most confusing thing
    /// a scrollback implementation can do.
    /// </summary>
    private void SnapToLiveScreen()
    {
        var moved = _viewport.ScrollToBottom();
        moved |= _viewport.ClearSelection();
        if (moved) InvalidateVisual();
    }

    /// <summary>
    /// A program copying to the clipboard with OSC 52 — Claude Code's copy, vim's "+y. Raised on the
    /// PTY reader thread under the engine's lock, so it is handed to the UI thread, which owns the
    /// clipboard.
    /// </summary>
    private void OnEngineClipboardWrite(string text)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        });
    }

    private void OnEngineNotification(TerminalNotification notification)
    {
        // Raised under the engine's lock on the PTY reader thread; never call back from there.
        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0) NotificationRequested?.Invoke(notification);
        });
    }

    // ---- focus reporting ------------------------------------------------------------------

    private Window? _focusWindow;
    private bool? _reportedFocus;

    /// <summary>
    /// Focused, for the program's purposes: this pane has the keyboard and WinMux is the active
    /// window. Switching to another application is looking away just as much as switching pane.
    /// </summary>
    private bool HasEffectiveFocus => IsKeyboardFocusWithin && _focusWindow?.IsActive == true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _focusWindow = TopLevel.GetTopLevel(this) as Window;
        if (_focusWindow is not null)
        {
            _focusWindow.Activated += OnWindowActivationChanged;
            _focusWindow.Deactivated += OnWindowActivationChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_focusWindow is not null)
        {
            _focusWindow.Activated -= OnWindowActivationChanged;
            _focusWindow.Deactivated -= OnWindowActivationChanged;
            _focusWindow = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowActivationChanged(object? sender, EventArgs e) => ReportFocus();

    /// <summary>
    /// Tell the program about a focus change, if it asked to be told and the state really changed.
    /// Only transitions are sent: a program that sees two focus-ins in a row may take the second
    /// as a fresh return from elsewhere.
    /// </summary>
    private void ReportFocus()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var focused = HasEffectiveFocus;
        if (_reportedFocus == focused) return;
        _reportedFocus = focused;

        if (!_engine.FocusReportingEnabled) return;
        _ = WriteFocusReportAsync(focused ? "\u001b[I"u8.ToArray() : "\u001b[O"u8.ToArray());
    }

    private async Task WriteFocusReportAsync(ReadOnlyMemory<byte> bytes)
    {
        // Not WriteInputAsync: a focus report is not typing, and must not snap a scrolled-back view
        // to the live screen.
        try
        {
            await WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (InvalidOperationException)
        {
            // Not started yet, or already exited: there is nobody to tell.
        }
    }

    private async Task WriteInputAsync(ReadOnlyMemory<byte> bytes)
    {
        if (TerminalDebugLog.Enabled) TerminalDebugLog.Write("  write to program: " + TerminalDebugLog.Describe(bytes.Span));
        SnapToLiveScreen();
        try
        {
            await WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    /// <summary>
    /// Copy the selection, or the visible screen when nothing is selected.
    ///
    /// Falling back rather than doing nothing keeps the behaviour that existed before selection
    /// did, and "copy what I am looking at" is a reasonable reading of the shortcut anyway.
    /// </summary>
    private Task CopySelectionAsync() =>
        _viewport.Selection is { } selection ? CopyRangeAsync(selection.Start, selection.End) : CopyVisibleTextAsync();

    private async Task CopyRangeAsync(TerminalPosition start, TerminalPosition end)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var columns = _engine.Columns;
        var cells = new TerminalCell[columns];
        var text = new StringBuilder();

        for (var row = start.Row; row <= end.Row && row < _engine.TotalRows; row++)
        {
            Array.Clear(cells);
            var info = _engine.CopyRow(row, cells);
            var length = Math.Min(info.Length, columns);

            var from = row == start.Row ? Math.Min(start.Column, length) : 0;
            var to = row == end.Row ? Math.Min(end.Column, length) : length;

            var line = new StringBuilder(Math.Max(0, to - from));
            for (var column = from; column < to; column++)
            {
                if (cells[column].IsWideTrailing) continue;
                if (cells[column].IsBlank || cells[column].Character == '\0') line.Append(' ');
                else cells[column].AppendGlyph(line);
            }

            // Trailing blanks are padding in a cell grid, not content. Every terminal trims them,
            // and pasting a copied path with forty spaces after it is a small misery.
            if (row < end.Row) text.AppendLine(line.ToString().TrimEnd());
            else text.Append(line.ToString().TrimEnd());
        }

        await clipboard.SetTextAsync(text.ToString());
        TerminalDebugLog.Write($"  copied {text.Length} characters to the clipboard");
    }

    private async Task CopyVisibleTextAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var cells = new TerminalCell[_engine.Columns];
        var text = new StringBuilder();
        var firstRow = Math.Max(0, _engine.TotalRows - _engine.Rows);
        for (var rowIndex = firstRow; rowIndex < _engine.TotalRows; rowIndex++)
        {
            Array.Clear(cells);
            var info = _engine.CopyRow(rowIndex, cells);
            var line = new StringBuilder(info.Length);
            for (var column = 0; column < info.Length; column++) cells[column].AppendGlyph(line);
            text.AppendLine(line.ToString().TrimEnd());
        }
        await clipboard.SetTextAsync(text.ToString().TrimEnd());
    }

    private async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) await PasteTextAsync(text);
    }

    private async Task PasteOrPassThroughAsync()
    {
        if (TerminalDebugLog.Enabled) await LogClipboardAsync("Ctrl+V");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            await PasteTextAsync(text);
            return;
        }

        await WriteInputAsync(new byte[] { 0x16 });
    }

    private static string Show(string? text) =>
        text is null ? "null" : "\"" + string.Concat(text.Select(c => c < ' ' ? $"<U+{(int)c:X4}>" : c.ToString())) + "\"";

    /// <summary>What the clipboard held when a paste key arrived — an image is what Claude Code looks for.</summary>
    private async Task LogClipboardAsync(string key)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                TerminalDebugLog.Write($"  clipboard at {key}: no clipboard available");
                return;
            }

            var formats = await clipboard.GetDataFormatsAsync();
            TerminalDebugLog.Write($"  clipboard at {key}: {string.Join(", ", formats.Select(f => f.ToString()))}");
        }
        catch (Exception ex)
        {
            TerminalDebugLog.Write($"  clipboard at {key}: could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Send pasted text the way the program asked for it — bracketed when it enabled that.</summary>
    private Task PasteTextAsync(string text) =>
        WriteInputAsync(TerminalInput.EncodePaste(text, _engine.BracketedPasteEnabled));
}
