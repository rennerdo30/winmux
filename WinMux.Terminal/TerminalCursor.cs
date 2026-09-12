namespace WinMux.Terminal;

public enum TerminalCursorStyle : byte
{
    BlinkingBlock,
    SteadyBlock,
    BlinkingUnderline,
    SteadyUnderline,
    BlinkingBar,
    SteadyBar,
}

/// <summary>Cursor state relative to the visible terminal screen.</summary>
public readonly record struct TerminalCursor(
    int Row,
    int Column,
    bool Visible,
    TerminalCursorStyle Style,
    bool WrapPending);
