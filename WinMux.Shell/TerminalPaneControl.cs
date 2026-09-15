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
    private const double FontSize = 14;
    private const double CellWidth = 8.45;
    private const double CellHeight = 18;

    private static readonly FontFamily TerminalFont = new("Cascadia Mono, Consolas, monospace");
    private static readonly Typeface TerminalTypeface = new(TerminalFont);
    private static readonly Typeface BoldTypeface = new(TerminalFont, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface ItalicTypeface = new(TerminalFont, FontStyle.Italic);
    private static readonly Typeface BoldItalicTypeface = new(TerminalFont, FontStyle.Italic, FontWeight.Bold);

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(TerminalPalette.DefaultBackground);
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xCC, 0xCC, 0xCC));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x78, 0xFF));
    private static readonly IBrush ScrollbarBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xCC, 0xCC, 0xCC));

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

        Focusable = true;
        ClipToBounds = true;
    }

    public event Action<string>? TitleChanged;

    public event Action<string>? WorkingDirectoryChanged;

    public event Action<int>? Exited;

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
        var visibleRows = Math.Min(_engine.Rows, Math.Max(0, (int)((Bounds.Height - 2 * Inset) / CellHeight)));
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

        DrawScrollbar(context, visibleRows);
    }

    /// <summary>Underlines, strikethrough and overline, which are lines rather than font choices.</summary>
    private static void DrawDecorations(
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

    private static Typeface TypefaceFor(TerminalCellAttributes attributes)
    {
        var bold = (attributes & TerminalCellAttributes.Bold) != 0;
        var italic = (attributes & TerminalCellAttributes.Italic) != 0;
        return (bold, italic) switch
        {
            (true, true) => BoldItalicTypeface,
            (true, false) => BoldTypeface,
            (false, true) => ItalicTypeface,
            _ => TerminalTypeface,
        };
    }

    /// <summary>The absolute cell under a point, clamped so a drag outside the control still works.</summary>
    private TerminalPosition PositionAt(Point point)
    {
        var columns = Math.Max(1, _engine.Columns);
        var column = Math.Clamp((int)Math.Round((point.X - Inset) / CellWidth), 0, columns);
        var row = _viewport.TopRow(_engine.TotalRows, _engine.Rows) +
                  Math.Clamp((int)((point.Y - Inset) / CellHeight), 0, Math.Max(0, _engine.Rows - 1));
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
        if (!_dragging) return;
        _viewport.ExtendSelection(PositionAt(e.GetPosition(this)));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
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
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        var at = PositionAt(point.Position);
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

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text))
        {
            _ = WriteInputAsync(Encoding.UTF8.GetBytes(e.Text));
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.C)
        {
            _ = CopySelectionAsync();
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

        ReadOnlyMemory<byte> bytes = default;
        var keyValue = (int)e.Key;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
            keyValue >= (int)Key.A &&
            keyValue <= (int)Key.Z)
        {
            bytes = new[] { (byte)(keyValue - (int)Key.A + 1) };
        }
        else
        {
            bytes = e.Key switch
            {
                Key.Enter => "\r"u8.ToArray(),
                Key.Back => new byte[] { 0x7f },
                Key.Left => "\u001b[D"u8.ToArray(),
                Key.Right => "\u001b[C"u8.ToArray(),
                Key.Up => "\u001b[A"u8.ToArray(),
                Key.Down => "\u001b[B"u8.ToArray(),
                Key.Home => "\u001b[H"u8.ToArray(),
                Key.End => "\u001b[F"u8.ToArray(),
                Key.Delete => "\u001b[3~"u8.ToArray(),
                Key.PageUp => "\u001b[5~"u8.ToArray(),
                Key.PageDown => "\u001b[6~"u8.ToArray(),
                Key.Tab => "\t"u8.ToArray(),
                Key.Escape => new byte[] { 0x1b },
                _ => default,
            };
        }

        if (!bytes.IsEmpty)
        {
            _ = WriteInputAsync(bytes);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
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

        _engine.Updated -= OnEngineUpdated;
        _engine.TitleChanged -= OnEngineTitleChanged;
        _engine.WorkingDirectoryChanged -= OnEngineWorkingDirectoryChanged;
        _engine.Response -= OnEngineResponse;
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

    private async Task PumpOutputAsync(IPtySession session, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await session.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
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

        var columns = Math.Max(2, (int)((Bounds.Width - 2 * Inset) / CellWidth));
        var rows = Math.Max(1, (int)((Bounds.Height - 2 * Inset) / CellHeight));
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
        _engine.Resize(columns, rows, reflow: true);
        lock (_gate)
        {
            _session?.Resize(columns, rows);
        }

        InvalidateVisual();
    }

    private void OnEngineUpdated()
    {
        if (_disposed != 0) return;

        Dispatcher.UIThread.Post(
            () =>
            {
                // Told about growth before redrawing: while the user is reading history, new
                // output must not drag the text upward under them.
                _viewport.OnBufferGrew(_engine.TotalRows, _engine.ScrollbackCount);
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

    private async Task WriteInputAsync(ReadOnlyMemory<byte> bytes)
    {
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
        if (!string.IsNullOrEmpty(text)) await SendText(text, _lifetime.Token);
    }
}
