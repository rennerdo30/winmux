using System.Text;
using Avalonia.Input;
using WinMux.Terminal;

namespace WinMux.Shell.Tests;

public sealed class TerminalWheelInputTests
{
    private static TerminalMouseMode Mouse => new(true, true, true, true, false);
    private static string Text(TerminalWheelAction action) => Encoding.ASCII.GetString(action.Bytes);

    [Theory]
    [InlineData(1, 64)]
    [InlineData(-1, 65)]
    public void Mouse_reporting_sends_SGR_wheel_presses_to_the_app(double delta, int button)
    {
        var action = new TerminalWheelInput().Route(delta, 10, 4, KeyModifiers.None, Mouse);
        Assert.Equal($"\u001b[<{button};11;5M", Text(action));
        Assert.Equal(0, action.HistoryRows);
    }

    [Fact]
    public void Modifiers_are_encoded_and_large_coordinates_do_not_wrap_in_SGR()
    {
        var action = new TerminalWheelInput().Route(1, 299, 49,
            KeyModifiers.Control | KeyModifiers.Alt, Mouse);
        Assert.Equal("\u001b[<88;300;50M", Text(action));
    }

    [Fact]
    public void Legacy_mouse_reports_are_bytes_not_UTF8()
    {
        var action = new TerminalWheelInput().Route(1, 150, 10, KeyModifiers.None, Mouse with { Sgr = false });
        Assert.Equal(new byte[] { 27, (byte)'[', (byte)'M', 96, 183, 43 }, action.Bytes);
    }

    [Fact]
    public void Legacy_coordinates_out_of_range_are_not_sent_as_wrong_positions()
    {
        var action = new TerminalWheelInput().Route(1, 300, 0, KeyModifiers.None, Mouse with { Sgr = false });
        Assert.Empty(action.Bytes);
        Assert.Equal(0, action.HistoryRows);
    }

    [Theory]
    [InlineData(false, "\u001b[A\u001b[A\u001b[A")]
    [InlineData(true, "\u001bOA\u001bOA\u001bOA")]
    public void Alternate_screen_without_mouse_reporting_translates_wheel_to_cursor_keys(bool application, string expected)
    {
        var mode = Mouse with { Reporting = false, ApplicationCursorKeys = application };
        Assert.Equal(expected, Text(new TerminalWheelInput().Route(1, 0, 0, KeyModifiers.None, mode)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Without_mouse_reporting_alternate_scroll_requires_both_modes(bool alternate, bool scroll)
    {
        var mode = Mouse with { Reporting = false, AlternateScreen = alternate, AlternateScroll = scroll };
        var action = new TerminalWheelInput().Route(1, 0, 0, KeyModifiers.None, mode);
        Assert.Empty(action.Bytes);
        Assert.Equal(3, action.HistoryRows);
    }

    [Fact]
    public void Shift_wheel_bypasses_the_app_and_moves_history()
    {
        var action = new TerminalWheelInput().Route(1, 0, 0, KeyModifiers.Shift, Mouse);
        Assert.Empty(action.Bytes);
        Assert.Equal(3, action.HistoryRows);
    }

    [Fact]
    public void Fractional_wheel_events_accumulate_instead_of_disappearing()
    {
        var wheel = new TerminalWheelInput();
        for (var i = 0; i < 3; i++) Assert.Empty(wheel.Route(.25, 0, 0, KeyModifiers.None, Mouse).Bytes);
        Assert.Equal("\u001b[<64;1;1M", Text(wheel.Route(.25, 0, 0, KeyModifiers.None, Mouse)));
    }

    [Fact]
    public void Changing_routes_does_not_leak_fractional_input_into_the_app()
    {
        var wheel = new TerminalWheelInput();
        wheel.Route(.25, 0, 0, KeyModifiers.Shift, Mouse);
        Assert.Empty(wheel.Route(.25, 0, 0, KeyModifiers.None, Mouse).Bytes);
    }
}
