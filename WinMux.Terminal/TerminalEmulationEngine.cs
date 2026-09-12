using EmulationCell = Terminal.Emulation.Cell;
using EmulationCellFlags = Terminal.Emulation.CellFlags;
using EmulationColor = Terminal.Emulation.TerminalColor;
using EmulationColorKind = Terminal.Emulation.ColorKind;
using EmulationCursorStyle = Terminal.Emulation.CursorStyle;
using EmulationTerminal = Terminal.Emulation.Terminal;

namespace WinMux.Terminal;

/// <summary>
/// Adapts the pinned Terminal.Emulation package to the WinMux-owned terminal contract.
/// </summary>
public sealed class TerminalEmulationEngine : ITerminalEngine
{
    private readonly object sync = new();
    private readonly EmulationTerminal terminal;

    public TerminalEmulationEngine(int columns, int rows, int scrollbackCapacity = 5_000)
    {
        ValidateDimensions(columns, rows);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollbackCapacity);

        terminal = new EmulationTerminal(columns, rows, scrollbackCapacity);
        terminal.Updated += OnUpdated;
        terminal.TitleChanged += OnTitleChanged;
        terminal.Response += OnResponse;
    }

    public int Columns
    {
        get { lock (sync) return terminal.Columns; }
    }

    public int Rows
    {
        get { lock (sync) return terminal.Rows; }
    }

    public int ScrollbackCount
    {
        get { lock (sync) return terminal.ScrollbackCount; }
    }

    public int TotalRows
    {
        get { lock (sync) return terminal.TotalRows; }
    }

    public long Version
    {
        get { lock (sync) return terminal.Version; }
    }

    public TerminalCursor Cursor
    {
        get
        {
            lock (sync)
            {
                return new TerminalCursor(
                    terminal.CursorRow,
                    terminal.CursorColumn,
                    terminal.Modes.CursorVisible,
                    ConvertCursorStyle(terminal.Modes.CursorStyle),
                    terminal.CursorWrapPending);
            }
        }
    }

    public string Title
    {
        get { lock (sync) return terminal.Title ?? string.Empty; }
    }

    public bool UsingAlternateScreen
    {
        get { lock (sync) return terminal.UsingAlternate; }
    }

    public int HyperlinkGeneration
    {
        get { lock (sync) return terminal.HyperlinkGeneration; }
    }

    public event Action? Updated;

    public event Action<string>? TitleChanged;

    public event Action<ReadOnlyMemory<byte>>? Response;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        lock (sync)
        {
            terminal.Write(bytes);
        }
    }

    public void Resize(int columns, int rows, bool reflow)
    {
        ValidateDimensions(columns, rows);

        lock (sync)
        {
            terminal.Resize(columns, rows, reflow);
        }
    }

    public TerminalRowInfo CopyRow(int rowIndex, Span<TerminalCell> destination)
    {
        lock (sync)
        {
            var source = terminal.GetRow(rowIndex);
            if (destination.Length < source.Length)
            {
                throw new ArgumentException(
                    $"Destination has {destination.Length} cells but row {rowIndex} requires {source.Length}.",
                    nameof(destination));
            }

            for (var column = 0; column < source.Length; column++)
            {
                destination[column] = ConvertCell(source.Cells[column]);
            }

            return new TerminalRowInfo(source.Length, source.Wrapped);
        }
    }

    public string? GetHyperlink(ushort linkId)
    {
        lock (sync)
        {
            return linkId == 0 ? null : terminal.GetHyperlink(linkId);
        }
    }

    private void OnUpdated() => Updated?.Invoke();

    private void OnTitleChanged(object? sender, string title) => TitleChanged?.Invoke(title);

    private void OnResponse(object? sender, ReadOnlyMemory<byte> bytes) => Response?.Invoke(bytes);

    private static void ValidateDimensions(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
    }

    private static TerminalCell ConvertCell(EmulationCell cell) =>
        new(
            cell.Char,
            cell.Extended,
            ConvertAttributes(cell.Flags),
            ConvertColor(cell.Foreground),
            ConvertColor(cell.Background),
            cell.Link,
            cell.IsBlank);

    private static TerminalCellAttributes ConvertAttributes(EmulationCellFlags flags)
    {
        var result = TerminalCellAttributes.None;
        Add(EmulationCellFlags.Bold, TerminalCellAttributes.Bold);
        Add(EmulationCellFlags.Faint, TerminalCellAttributes.Faint);
        Add(EmulationCellFlags.Italic, TerminalCellAttributes.Italic);
        Add(EmulationCellFlags.Underline, TerminalCellAttributes.Underline);
        Add(EmulationCellFlags.Blink, TerminalCellAttributes.Blink);
        Add(EmulationCellFlags.Inverse, TerminalCellAttributes.Inverse);
        Add(EmulationCellFlags.Invisible, TerminalCellAttributes.Invisible);
        Add(EmulationCellFlags.Strikethrough, TerminalCellAttributes.Strikethrough);
        Add(EmulationCellFlags.DoubleUnderline, TerminalCellAttributes.DoubleUnderline);
        Add(EmulationCellFlags.CurlyUnderline, TerminalCellAttributes.CurlyUnderline);
        Add(EmulationCellFlags.Overline, TerminalCellAttributes.Overline);
        Add(EmulationCellFlags.WideLeading, TerminalCellAttributes.WideLeading);
        Add(EmulationCellFlags.WideTrailing, TerminalCellAttributes.WideTrailing);
        return result;

        void Add(EmulationCellFlags source, TerminalCellAttributes target)
        {
            if ((flags & source) != 0)
            {
                result |= target;
            }
        }
    }

    private static TerminalColor ConvertColor(EmulationColor color) =>
        color.Kind switch
        {
            EmulationColorKind.Default => TerminalColor.Default,
            EmulationColorKind.Palette => TerminalColor.FromPalette(color.Index),
            EmulationColorKind.Rgb => TerminalColor.FromRgb(color.R, color.G, color.B),
            _ => throw new InvalidOperationException($"Unknown terminal color kind: {color.Kind}."),
        };

    private static TerminalCursorStyle ConvertCursorStyle(EmulationCursorStyle style) =>
        style switch
        {
            EmulationCursorStyle.BlinkingBlock => TerminalCursorStyle.BlinkingBlock,
            EmulationCursorStyle.SteadyBlock => TerminalCursorStyle.SteadyBlock,
            EmulationCursorStyle.BlinkingUnderline => TerminalCursorStyle.BlinkingUnderline,
            EmulationCursorStyle.SteadyUnderline => TerminalCursorStyle.SteadyUnderline,
            EmulationCursorStyle.BlinkingBar => TerminalCursorStyle.BlinkingBar,
            EmulationCursorStyle.SteadyBar => TerminalCursorStyle.SteadyBar,
            _ => throw new InvalidOperationException($"Unknown terminal cursor style: {style}."),
        };
}
