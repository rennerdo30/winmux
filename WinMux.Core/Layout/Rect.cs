namespace WinMux.Core.Layout;

/// <summary>
/// An integer rectangle in device pixels.
///
/// Defined here rather than borrowed from a UI framework because `WinMux.Core` must not reference
/// any platform assembly (CLAUDE.md section 3), and because the layout engine is the one part of
/// the product that is meant to be portable by construction.
/// </summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    public Rect WithSize(int width, int height) => this with { Width = width, Height = height };

    /// <summary>Shrink on every side. Never produces a negative extent.</summary>
    public Rect Deflate(int amount)
    {
        var w = Math.Max(0, Width - 2 * amount);
        var h = Math.Max(0, Height - 2 * amount);
        return new Rect(X + amount, Y + amount, w, h);
    }

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>Do the two rectangles overlap along the vertical axis at all?</summary>
    public bool OverlapsVertically(Rect other) => Top < other.Bottom && other.Top < Bottom;

    /// <summary>Do the two rectangles overlap along the horizontal axis at all?</summary>
    public bool OverlapsHorizontally(Rect other) => Left < other.Right && other.Left < Right;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
