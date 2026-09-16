namespace WinMux.Platform;

/// <summary>
/// The picture an application shows for itself.
///
/// Behind the platform boundary for the usual reason (ADR 0013): an icon lives inside a PE
/// executable on Windows, in a `.desktop` file and an icon theme on Linux, and in a bundle on macOS.
/// None of that is expressible portably, and none of it belongs in the shell.
///
/// The contract deliberately hands back **PNG bytes** rather than any drawing type. `WinMux.Platform`
/// targets `net10.0` with no UI framework, and a byte array is something every toolkit can turn into
/// its own bitmap — the alternative would drag Avalonia, or worse `System.Drawing`, across a boundary
/// that exists to keep both out.
/// </summary>
public interface IAppIconSource
{
    /// <summary>Whether this system can produce icons at all. False means every call returns null.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The icon for <paramref name="programPath"/> as PNG bytes, or null when there is none to be
    /// had.
    /// </summary>
    /// <param name="programPath">The executable. A shortcut should be resolved before asking.</param>
    /// <param name="size">The wanted edge length in pixels; the result may differ.</param>
    /// <remarks>
    /// Null is an ordinary answer, not a failure: plenty of executables carry no icon, and a file
    /// can be deleted between the catalogue being read and the picker being drawn. Callers show a
    /// placeholder rather than an error — a missing icon must never be louder than the name beside
    /// it.
    /// </remarks>
    byte[]? GetIconPng(string programPath, int size = 32);
}
