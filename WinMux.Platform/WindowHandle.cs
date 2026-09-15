namespace WinMux.Platform;

/// <summary>
/// An opaque reference to a window owned by the windowing system.
///
/// Deliberately not named after any platform primitive and deliberately not an <c>IntPtr</c> in
/// consuming code: `HWND` on Windows, an X11 `Window` id elsewhere. Callers may compare, store and
/// pass these around, and may not interpret them — only a <c>WinMux.Platform.*</c> implementation
/// knows what the number means.
/// </summary>
public readonly record struct WindowHandle(nint Value)
{
    public static readonly WindowHandle None = new(0);

    public bool IsNone => Value == 0;

    /// <summary>
    /// The raw value, for handing back to the windowing system or across a process boundary.
    /// Named so that a reader can see the boundary being crossed rather than an innocent cast.
    /// </summary>
    public nint ToPlatformValue() => Value;

    public static WindowHandle FromPlatformValue(nint value) => new(value);

    public override string ToString() => IsNone ? "(none)" : "0x" + Value.ToString("X");
}
