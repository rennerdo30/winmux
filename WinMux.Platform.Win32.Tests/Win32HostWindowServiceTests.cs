using WinMux.Core.Layout;
using WinMux.Platform;
using WinMux.Platform.Win32.Windows;

namespace WinMux.Platform.Win32.Tests;

/// <summary>
/// What can honestly be asserted about the window service without a window.
///
/// Everything it actually does is a synchronous call against a foreign process, which is precisely
/// what ADR 0001 forbids a test — or the UI thread — from waiting on. So the interesting property
/// is the one that keeps the shell safe: a handle that is not a live window is never touched. The
/// rest is verified by the human walkthrough in HANDOFF.md, and that is stated rather than faked.
/// </summary>
public sealed class Win32HostWindowServiceTests
{
    private readonly Win32HostWindowService _windows = new();

    [Fact]
    public void A_null_handle_is_not_alive()
    {
        Assert.False(_windows.IsAlive(WindowHandle.None));
    }

    [Fact]
    public void A_handle_that_is_not_a_window_is_not_alive()
    {
        Assert.False(_windows.IsAlive(WindowHandle.FromPlatformValue(0x7FFF_FFFF)));
    }

    [Fact]
    public void Placing_a_dead_window_is_a_no_op_rather_than_an_exception()
    {
        var placement = new WindowPlacement(new Rect(0, 0, 100, 100), WindowPlacementMode.EmbeddedChild, true, true);

        _windows.Apply(WindowHandle.None, placement);
    }

    [Fact]
    public void A_placement_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => _windows.Apply(WindowHandle.None, null!));
    }
}
