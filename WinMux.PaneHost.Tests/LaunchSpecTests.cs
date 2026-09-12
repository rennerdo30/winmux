using WinMux.PaneHost;

namespace WinMux.PaneHost.Tests;

public sealed class LaunchSpecTests
{
    [Fact]
    public void Embed_is_the_backwards_compatible_default()
    {
        var spec = LaunchSpec.Parse(["--program", "notepad.exe"]);

        Assert.Equal(HostStrategy.Embed, spec.Strategy);
        Assert.Equal(400, spec.SettleMilliseconds);
        Assert.Equal(WindowMatchMode.ProcessName, spec.MatchMode);
    }

    [Theory]
    [InlineData("embed", "Embed")]
    [InlineData("EMBED", "Embed")]
    [InlineData("attach", "Attach")]
    [InlineData("ATTACH", "Attach")]
    public void Strategy_is_explicit_and_case_insensitive(string value, string expected)
    {
        var spec = LaunchSpec.Parse(["--program", "app.exe", "--strategy", value]);

        Assert.Equal(expected, spec.Strategy.ToString());
    }

    [Fact]
    public void Invalid_strategy_has_a_plain_error()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LaunchSpec.Parse(["--program", "app.exe", "--strategy", "magic"]));

        Assert.Equal("--strategy must be either embed or attach", error.Message);
    }

    [Fact]
    public void Missing_strategy_value_has_a_plain_error()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LaunchSpec.Parse(["--program", "app.exe", "--strategy"]));

        Assert.Equal("missing value for --strategy", error.Message);
    }

    [Fact]
    public void Arguments_after_separator_are_not_parsed_as_host_options()
    {
        var spec = LaunchSpec.Parse([
            "--program", "app.exe", "--strategy", "attach", "--", "--strategy", "embed"]);

        Assert.Equal(HostStrategy.Attach, spec.Strategy);
        Assert.Equal(["--strategy", "embed"], spec.Arguments);
    }

    [Fact]
    public void Settle_time_is_configurable()
    {
        var spec = LaunchSpec.Parse(["--program", "app.exe", "--settle-ms", "2500"]);

        Assert.Equal(2500, spec.SettleMilliseconds);
    }

    [Fact]
    public void Title_filter_is_passed_to_window_discovery()
    {
        var spec = LaunchSpec.Parse([
            "--program", "app.exe", "--window-title-contains", "Project Alpha"]);

        Assert.Equal("Project Alpha", spec.WindowTitleContains);
    }

    [Fact]
    public void Window_class_selects_class_matching_for_backwards_compatibility()
    {
        var spec = LaunchSpec.Parse(["--program", "app.exe", "--window-class", "MainWindow"]);

        Assert.Equal(WindowMatchMode.ClassName, spec.MatchMode);
    }

    [Fact]
    public void Process_name_override_is_passed_to_window_discovery()
    {
        var spec = LaunchSpec.Parse(["--program", "shim.exe", "--process-name", "real.exe"]);

        Assert.Equal("real.exe", spec.ProcessName);
    }

    [Theory]
    [InlineData("pid", "Pid")]
    [InlineData("process-name", "ProcessName")]
    [InlineData("class-name", "ClassName")]
    public void Match_mode_is_explicit(string value, string expected)
    {
        var spec = LaunchSpec.Parse(["--program", "app.exe", "--match-mode", value]);

        Assert.Equal(expected, spec.MatchMode.ToString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("later")]
    public void Invalid_settle_time_has_a_plain_error(string value)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LaunchSpec.Parse(["--program", "app.exe", "--settle-ms", value]));

        Assert.Equal("--settle-ms must be a non-negative integer", error.Message);
    }
}
