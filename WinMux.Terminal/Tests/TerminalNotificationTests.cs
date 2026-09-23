using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

/// <summary>
/// A program asking for attention, in each of the forms programs use.
///
/// The hazards are the ones that would make this either useless or noisy: a ConEmu OSC 9 subcommand
/// (9;9 is the working directory, 9;4 a progress bar) mistaken for a message; the BEL that ends an
/// OSC mistaken for a bell; and whatever arrives in the payload reaching a notification unfiltered.
/// </summary>
public sealed class TerminalNotificationTests
{
    [Fact]
    public void Osc9_is_a_message()
    {
        var (engine, heard) = Listen();

        engine.Write(Encoding.UTF8.GetBytes("\e]9;Claude needs your permission to use Bash\a"));

        var notification = Assert.Single(heard);
        Assert.Equal(TerminalNotificationKind.Osc9, notification.Kind);
        Assert.Null(notification.Title);
        Assert.Equal("Claude needs your permission to use Bash", notification.Body);
    }

    [Theory]
    [InlineData("\e]9;9;C:\\Work\a")]
    [InlineData("\e]9;4;1;50\a")]
    [InlineData("\e]9;4\a")]
    [InlineData("\e]9;12\a")]
    public void ConEmu_subcommands_are_not_messages(string sequence)
    {
        var (engine, heard) = Listen();

        engine.Write(Encoding.UTF8.GetBytes(sequence));

        Assert.Empty(heard);
    }

    [Fact]
    public void The_working_directory_report_still_arrives_and_is_not_also_a_notification()
    {
        var (engine, heard) = Listen();
        var paths = new List<string>();
        engine.WorkingDirectoryChanged += paths.Add;

        engine.Write(Encoding.UTF8.GetBytes("\e]9;9;D:\\Projects\a"));

        Assert.Equal(["D:\\Projects"], paths);
        Assert.Empty(heard);
    }

    [Fact]
    public void Osc777_has_a_title_and_a_body_that_may_contain_semicolons()
    {
        var (engine, heard) = Listen();

        engine.Write(Encoding.UTF8.GetBytes("\e]777;notify;Build;done; 3 warnings\e\\"));

        var notification = Assert.Single(heard);
        Assert.Equal(TerminalNotificationKind.Osc777, notification.Kind);
        Assert.Equal("Build", notification.Title);
        Assert.Equal("done; 3 warnings", notification.Body);
    }

    [Fact]
    public void Osc99_in_one_part()
    {
        var (engine, heard) = Listen();

        engine.Write(Encoding.UTF8.GetBytes("\e]99;;Hello from kitty\e\\"));

        Assert.Equal("Hello from kitty", Assert.Single(heard).Body);
    }

    [Fact]
    public void Osc99_title_and_body_in_two_parts_one_of_them_base64()
    {
        var (engine, heard) = Listen();
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("Tests passed"));

        engine.Write(Encoding.UTF8.GetBytes("\e]99;i=7:d=0;CI\e\\"));
        Assert.Empty(heard);
        engine.Write(Encoding.UTF8.GetBytes($"\e]99;i=7:p=body:e=1;{body}\e\\"));

        var notification = Assert.Single(heard);
        Assert.Equal("CI", notification.Title);
        Assert.Equal("Tests passed", notification.Body);
    }

    [Fact]
    public void A_bare_bell_is_a_bell_but_the_bell_that_ends_an_osc_is_not()
    {
        var (engine, heard) = Listen();

        engine.Write(Encoding.UTF8.GetBytes("\e]0;window title\a"));
        Assert.Empty(heard);

        engine.Write(Encoding.UTF8.GetBytes("done\a"));
        Assert.Equal(TerminalNotificationKind.Bell, Assert.Single(heard).Kind);
    }

    [Fact]
    public void A_message_split_at_every_byte_still_arrives_once()
    {
        var (engine, heard) = Listen();

        foreach (var value in Encoding.UTF8.GetBytes("\e]9;Réponse prête ✓\e\\"))
        {
            engine.Write([value]);
        }

        Assert.Equal("Réponse prête ✓", Assert.Single(heard).Body);
    }

    [Fact]
    public void Control_characters_are_stripped_and_length_is_bounded()
    {
        var (engine, heard) = Listen();
        var longText = new string('x', 2000);

        engine.Write(Encoding.UTF8.GetBytes($"\e]9;line one\tline\u0001two {longText}\a"));

        var body = Assert.Single(heard).Body;
        Assert.StartsWith("line one line two", body);
        Assert.DoesNotContain('\t', body);
        Assert.DoesNotContain('\u0001', body);
        Assert.True(body.Length <= 501, "500 characters and an ellipsis at most");
    }

    [Fact]
    public void Focus_reporting_follows_the_programs_request()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        Assert.False(engine.FocusReportingEnabled);

        engine.Write(Encoding.UTF8.GetBytes("\e[?1004h"));
        Assert.True(engine.FocusReportingEnabled);

        engine.Write(Encoding.UTF8.GetBytes("\e[?1004l"));
        Assert.False(engine.FocusReportingEnabled);
    }

    [Fact]
    public void Osc52_puts_text_on_the_clipboard()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var copied = new List<string>();
        engine.ClipboardWriteRequested += copied.Add;
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("copied by Claude ✓"));

        engine.Write(Encoding.UTF8.GetBytes($"\e]52;c;{payload}\a"));

        Assert.Equal(["copied by Claude ✓"], copied);
    }

    [Fact]
    public void Osc52_read_requests_are_never_answered()
    {
        // Answering "?" would let anything printed in a pane read the user's clipboard.
        var engine = new TerminalEmulationEngine(40, 4);
        var copied = new List<string>();
        var responses = new List<byte[]>();
        engine.ClipboardWriteRequested += copied.Add;
        engine.Response += bytes => responses.Add(bytes.ToArray());

        engine.Write(Encoding.UTF8.GetBytes("\e]52;c;?\a"));

        Assert.Empty(copied);
        Assert.Empty(responses);
    }

    [Fact]
    public void Osc52_carries_a_long_passage()
    {
        // Base64 of 100 KB is more than the old 16 KB OSC limit, which silently dropped such copies.
        var engine = new TerminalEmulationEngine(40, 4);
        var copied = new List<string>();
        engine.ClipboardWriteRequested += copied.Add;
        var text = new string('x', 100_000);

        engine.Write(Encoding.UTF8.GetBytes($"\e]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\a"));

        Assert.Equal(text, Assert.Single(copied));
    }

    [Fact]
    public void Bracketed_paste_follows_the_programs_request()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        Assert.False(engine.BracketedPasteEnabled);
        engine.Write(Encoding.UTF8.GetBytes("\e[?2004h"));
        Assert.True(engine.BracketedPasteEnabled);
    }

    private static (TerminalEmulationEngine Engine, List<TerminalNotification> Heard) Listen()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var heard = new List<TerminalNotification>();
        engine.NotificationRequested += heard.Add;
        return (engine, heard);
    }
}
