using System.Text;

namespace WinMux.Terminal;

[Flags]
public enum TerminalCellAttributes : ushort
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Invisible = 1 << 6,
    Strikethrough = 1 << 7,
    DoubleUnderline = 1 << 8,
    CurlyUnderline = 1 << 9,
    Overline = 1 << 10,
    WideLeading = 1 << 14,
    WideTrailing = 1 << 15,
}

/// <summary>A renderer-facing copy of one terminal grid cell.</summary>
public readonly record struct TerminalCell(
    char Character,
    string? ExtendedGlyph,
    TerminalCellAttributes Attributes,
    TerminalColor Foreground,
    TerminalColor Background,
    ushort HyperlinkId,
    bool IsBlank)
{
    public bool IsWideLeading => (Attributes & TerminalCellAttributes.WideLeading) != 0;

    public bool IsWideTrailing => (Attributes & TerminalCellAttributes.WideTrailing) != 0;

    /// <summary>Appends this cell's grapheme without allocating a one-character string.</summary>
    public void AppendGlyph(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (IsBlank || IsWideTrailing)
        {
            return;
        }

        if (ExtendedGlyph is not null)
        {
            builder.Append(ExtendedGlyph);
        }
        else
        {
            builder.Append(Character);
        }
    }
}

/// <summary>Metadata returned with a copied terminal row.</summary>
public readonly record struct TerminalRowInfo(int Length, bool Wrapped);
