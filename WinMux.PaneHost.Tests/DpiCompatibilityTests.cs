using WinMux.PaneHost;

namespace WinMux.PaneHost.Tests;

public sealed class DpiCompatibilityTests
{
    [Theory]
    [InlineData(0, 144, true)]
    [InlineData(1, 144, true)]
    [InlineData(2, 144, false)]
    [InlineData(0, 96, false)]
    [InlineData(-1, 144, false)]
    public void Attach_only_when_non_per_monitor_app_would_lose_scaling(int awareness, uint monitorDpi, bool expected)
    {
        Assert.Equal(expected, DpiCompatibility.NeedsAttach(awareness, monitorDpi));
    }

    [Theory]
    [InlineData(0, 144, true)]
    [InlineData(1, 144, true)]
    [InlineData(2, 144, false)]
    [InlineData(0, 96, false)]
    public void Live_embed_request_uses_the_same_dpi_fallback_as_initial_hosting(
        int awareness, uint monitorDpi, bool attach)
    {
        Assert.Equal(attach ? HostStrategy.Attach : HostStrategy.Embed,
            DpiCompatibility.EffectiveStrategy(HostStrategy.Embed, awareness, monitorDpi));
        Assert.Equal(HostStrategy.Attach,
            DpiCompatibility.EffectiveStrategy(HostStrategy.Attach, awareness, monitorDpi));
    }
}
