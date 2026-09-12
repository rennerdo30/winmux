using WinMux.Core.Model;

namespace WinMux.Shell.Tests;

public sealed class ForeignAppProtocolTests
{
    [Theory]
    [InlineData("READY=123", HostStrategy.Embed)]
    [InlineData("READY=123;strategy=embed", HostStrategy.Embed)]
    [InlineData("READY=123;strategy=attach", HostStrategy.Attach)]
    public void Ready_parser_accepts_legacy_and_strategy_aware_messages(string line, HostStrategy expected)
    {
        Assert.True(ForeignAppPane.TryReadReady(line, out var hwnd, out var strategy));
        Assert.Equal(new IntPtr(123), hwnd);
        Assert.Equal(expected, strategy);
    }

    [Theory]
    [InlineData("READY=0")]
    [InlineData("READY=nope;strategy=attach")]
    [InlineData("ERROR=not ready")]
    public void Ready_parser_rejects_invalid_messages(string line)
    {
        Assert.False(ForeignAppPane.TryReadReady(line, out _, out _));
    }
}
