namespace WinMux.Terminal;

public enum TerminalColorKind : byte
{
    Default,
    Palette,
    Rgb,
}

/// <summary>A theme-default, palette-indexed, or 24-bit terminal color.</summary>
public readonly record struct TerminalColor(
    TerminalColorKind Kind,
    byte PaletteIndex,
    byte Red,
    byte Green,
    byte Blue)
{
    public static TerminalColor Default { get; } = new(TerminalColorKind.Default, 0, 0, 0, 0);

    public static TerminalColor FromPalette(byte index) =>
        new(TerminalColorKind.Palette, index, 0, 0, 0);

    public static TerminalColor FromRgb(byte red, byte green, byte blue) =>
        new(TerminalColorKind.Rgb, 0, red, green, blue);
}
