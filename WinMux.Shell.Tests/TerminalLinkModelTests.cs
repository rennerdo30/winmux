using WinMux.Terminal;

namespace WinMux.Shell.Tests;

/// <summary>
/// Finding the link under a cell. Pure arithmetic over a row, so none of this needs a terminal.
/// </summary>
public class TerminalLinkModelTests
{
    private static TerminalCell[] Row(string text, ushort link = 0) =>
        [.. text.Select(c => new TerminalCell(c, null, TerminalCellAttributes.None,
            TerminalColor.Default, TerminalColor.Default, link, IsBlank: c == ' '))];

    private static string? NoLinks(ushort id) => null;

    [Fact]
    public void A_url_in_ordinary_output_is_found()
    {
        var row = Row("see https://example.com/x for more");

        var link = TerminalLinkModel.At(row, column: 10, NoLinks);

        Assert.Equal("https://example.com/x", link?.Url);
        Assert.Equal(4, link?.Start);
        Assert.Equal(25, link?.End);
    }

    [Fact]
    public void Text_beside_a_url_is_not_part_of_it()
    {
        var row = Row("see https://example.com/x for more");

        Assert.Null(TerminalLinkModel.At(row, column: 2, NoLinks));
        Assert.Null(TerminalLinkModel.At(row, column: 27, NoLinks));
    }

    [Fact]
    public void A_full_stop_after_a_url_belongs_to_the_sentence()
    {
        var row = Row("go to https://example.com/page.");

        Assert.Equal("https://example.com/page", TerminalLinkModel.At(row, column: 10, NoLinks)?.Url);
    }

    [Fact]
    public void A_bracket_the_url_opened_is_kept()
    {
        Assert.Equal("https://example.com/a(b)", TerminalLinkModel.Trim("https://example.com/a(b)"));
        Assert.Equal("https://example.com/a", TerminalLinkModel.Trim("https://example.com/a)"));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:notifications")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vscode://file/C:/x")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Only_http_and_https_will_ever_be_opened(string? url)
    {
        // A pane shows the output of whatever is running in it, and that output is not trusted. A
        // custom scheme is a registered program with an argument the writer chose.
        Assert.False(TerminalLinkModel.IsOpenable(url));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/a/b?c=d#e")]
    public void Http_and_https_open(string url) => Assert.True(TerminalLinkModel.IsOpenable(url));

    [Fact]
    public void An_osc_8_hyperlink_covers_exactly_the_cells_it_marked()
    {
        // The case a pattern cannot do: the text says one thing and the link points somewhere else.
        var cells = new List<TerminalCell>();
        cells.AddRange(Row("see "));
        cells.AddRange(Row("the docs", link: 7));
        cells.AddRange(Row(" now"));

        var link = TerminalLinkModel.At(
            cells.ToArray(), column: 6, id => id == 7 ? "https://winmux.dev/docs" : null);

        Assert.Equal("https://winmux.dev/docs", link?.Url);
        Assert.Equal(4, link?.Start);
        Assert.Equal(12, link?.End);
    }

    [Fact]
    public void An_osc_8_link_to_a_scheme_we_will_not_open_is_not_a_link()
    {
        var cells = Row("click here", link: 3);

        Assert.Null(TerminalLinkModel.At(cells, column: 2, _ => "file:///C:/Windows/System32/cmd.exe"));
    }

    [Fact]
    public void A_column_off_the_end_of_the_row_is_not_a_crash()
    {
        var row = Row("https://example.com");

        Assert.Null(TerminalLinkModel.At(row, column: 500, NoLinks));
        Assert.Null(TerminalLinkModel.At(row, column: -1, NoLinks));
    }
}
