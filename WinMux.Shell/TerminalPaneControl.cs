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

    private static readonly Typeface TerminalTypeface = new("Cascadia Mono, Consolas, monospace");
    private static readonly IBrush ForegroundBrush = new SolidColorBrush(Color.FromRgb(0xd8, 0xde, 0xe9));
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18));
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x88, 0xc0, 0xd0));
    private static readonly IBrush FocusBrush = new SolidColorBrush(Color.FromRgb(0x5e, 0x81, 0xac));

    private readonly object _gate = new();
    private readonly string _program;
    private readonly string[] _arguments;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string> _environment;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITerminalEngine _engine;

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
        _engine.Response += OnEngineResponse;

        Focusable = true;
        ClipToBounds = true;
    }

    public event Action<string>? TitleChanged;

    public event Action<int>? Exited;

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
        var renderBounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, renderBounds, 0);

        var cursor = _engine.Cursor;
        if (cursor.Visible && cursor.Row >= 0 && cursor.Row < _engine.Rows)
        {
            context.FillRectangle(
                CursorBrush,
                new Rect(cursor.Column * CellWidth, cursor.Row * CellHeight, CellWidth, CellHeight),
                0);
        }

        var columns = _engine.Columns;
        var visibleRows = Math.Min(_engine.Rows, Math.Max(0, (int)(Bounds.Height / CellHeight)));
        var firstRow = Math.Max(0, _engine.TotalRows - _engine.Rows);
        var cells = new TerminalCell[columns];
        var line = new StringBuilder(columns);

        for (var row = 0; row < visibleRows; row++)
        {
            Array.Clear(cells);
            var info = _engine.CopyRow(firstRow + row, cells);
            line.Clear();
            for (var column = 0; column < Math.Min(info.Length, columns); column++)
            {
                var cell = cells[column];
                if (cell.IsWideTrailing)
                {
                    continue;
                }

                if (cell.IsBlank || (cell.Character == '\0' && cell.ExtendedGlyph is null))
                {
                    line.Append(' ');
                }
                else
                {
                    cell.AppendGlyph(line);
                }
            }

            var text = new FormattedText(
                line.ToString(),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                TerminalTypeface,
                FontSize,
                ForegroundBrush);
            context.DrawText(text, new Point(0, row * CellHeight));
        }

        if (IsKeyboardFocusWithin)
        {
            context.DrawRectangle(new Pen(FocusBrush), renderBounds.Deflate(0.5), 0);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
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
            _ = CopyVisibleTextAsync();
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

        var columns = Math.Max(2, (int)(Bounds.Width / CellWidth));
        var rows = Math.Max(1, (int)(Bounds.Height / CellHeight));
        if (columns == _engine.Columns && rows == _engine.Rows)
        {
            return;
        }

        _engine.Resize(columns, rows, reflow: false);
        lock (_gate)
        {
            _session?.Resize(columns, rows);
        }

        InvalidateVisual();
    }

    private void OnEngineUpdated()
    {
        if (_disposed == 0)
        {
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }

    private void OnEngineTitleChanged(string title)
    {
        if (_disposed == 0)
        {
            Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
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

    private async Task WriteInputAsync(ReadOnlyMemory<byte> bytes)
    {
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
