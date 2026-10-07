using Xunit;

namespace WinMux.Terminal.Tests;

public sealed class MouseModeTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void Mouse_reporting_modes_are_exposed_to_the_host(int mode)
    {
        var engine = new TerminalEmulationEngine(80, 24);
        engine.Write(System.Text.Encoding.ASCII.GetBytes($"\u001b[?{mode}h\u001b[?1006h"));
        Assert.True(engine.MouseMode.Reporting);
        Assert.True(engine.MouseMode.Sgr);
        engine.Write(System.Text.Encoding.ASCII.GetBytes($"\u001b[?{mode}l"));
        Assert.False(engine.MouseMode.Reporting);
    }

    [Fact]
    public void Alternate_scroll_tracks_the_app_request_and_screen()
    {
        var engine = new TerminalEmulationEngine(80, 24);
        engine.Write("\u001b[?1049h\u001b[?1007h\u001b[?1h"u8);
        Assert.True(engine.MouseMode.AlternateScreen);
        Assert.True(engine.MouseMode.AlternateScroll);
        Assert.True(engine.MouseMode.ApplicationCursorKeys);
        engine.Write("\u001b[?1049l\u001b[?1007l\u001b[?1l"u8);
        Assert.False(engine.MouseMode.AlternateScreen);
        Assert.False(engine.MouseMode.AlternateScroll);
        Assert.False(engine.MouseMode.ApplicationCursorKeys);
    }
}
