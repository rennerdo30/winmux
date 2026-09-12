using WinMux.PaneHost;

namespace WinMux.PaneHost.Tests;

public sealed class LaunchSpecTests
{
    [Fact]
    public void Embed_is_the_backwards_compatible_default()
    {
        var spec = LaunchSpec.Parse(["--program", "notepad.exe"]);

        Assert.Equal(HostStrategy.Embed, spec.Strategy);
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
}
