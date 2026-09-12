using WinMux.PaneHost;

namespace WinMux.PaneHost.Tests;

public sealed class HostProtocolTests
{
    [Theory]
    [InlineData("embed", "STRATEGY=embed")]
    [InlineData("attach", "STRATEGY=attach")]
    public void Strategy_identifies_the_effective_mode(string strategyName, string expected)
    {
        var strategy = strategyName == "embed" ? HostStrategy.Embed : HostStrategy.Attach;
        Assert.Equal(expected, HostProtocol.Strategy(strategy));
    }

    [Fact]
    public void Ready_remains_compatible_with_the_existing_numeric_protocol()
    {
        Assert.Equal("READY=1234", HostProtocol.Ready(new IntPtr(1234)));
    }

    [Fact]
    public void Notice_is_machine_detectable_without_being_a_terminal_error()
    {
        Assert.Equal("NOTICE=fell back safely", HostProtocol.Notice("fell back safely"));
    }

    [Theory]
    [InlineData(5, "elevated or higher-integrity", "win32=5", "fallback=attach")]
    [InlineData(87, "refused embedding", "win32=87", "fallback=attach")]
    public void Known_embed_failures_are_plain_and_machine_detectable(
        int error,
        string explanation,
        string code,
        string fallback)
    {
        var result = HostProtocol.EmbedFailure(error);

        Assert.StartsWith("ERROR=", result);
        Assert.Contains(explanation, result);
        Assert.Contains(code, result);
        Assert.Contains(fallback, result);
    }

    [Fact]
    public void Unknown_embed_failure_keeps_the_win32_code()
    {
        Assert.Equal(
            "ERROR=SetParent failed with Win32 error 1400 (fallback=attach)",
            HostProtocol.EmbedFailure(1400));
    }

    [Fact]
    public void Parent_verification_failure_offers_attach()
    {
        Assert.Equal(
            "ERROR=application did not become a child of PaneHost (win32=0, fallback=attach)",
            HostProtocol.EmbedFailure(0));
    }
}
