using WinMux.Core.Model;

namespace WinMux.Tests;

public sealed class PaneKindTests
{
    [Fact]
    public void Built_in_values_keep_the_published_session_spellings()
    {
        Assert.Equal("terminal", PaneKind.Terminal.Value);
        Assert.Equal("file-browser", PaneKind.FileBrowser.Value);
        Assert.Equal("foreign-app", PaneKind.ForeignApp.Value);
    }

    [Fact]
    public void Third_party_identifiers_are_normalized_and_compare_by_stable_value()
    {
        var mixedCase = PaneKind.Create("Com.Example-Preview_2");
        var canonical = new PaneKind("com.example-preview_2");

        Assert.Equal("com.example-preview_2", mixedCase.Value);
        Assert.Equal(canonical, mixedCase);
        Assert.Equal(canonical.GetHashCode(), mixedCase.GetHashCode());
        Assert.True(PaneKind.Create("com.example.alpha") < PaneKind.Create("com.example.beta"));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("provider2")]
    [InlineData("com.example.preview")]
    [InlineData("example-file_browser")]
    public void Stable_third_party_identifiers_are_accepted(string value)
    {
        Assert.True(PaneKind.TryCreate(value, out var kind));
        Assert.Equal(value, kind.Value);
        Assert.True(kind.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" provider")]
    [InlineData("provider ")]
    [InlineData("2provider")]
    [InlineData("provider..preview")]
    [InlineData("provider-")]
    [InlineData("provider/preview")]
    [InlineData("prøvider")]
    public void Unstable_identifiers_are_rejected(string? value)
    {
        Assert.False(PaneKind.TryCreate(value, out var kind));
        Assert.False(kind.IsValid);
    }

    [Fact]
    public void Default_value_is_explicitly_invalid()
    {
        var kind = default(PaneKind);

        Assert.False(kind.IsValid);
        Assert.Throws<InvalidOperationException>(() => kind.Value);
    }
}
