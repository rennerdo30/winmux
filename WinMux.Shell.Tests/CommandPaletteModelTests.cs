using Avalonia.Input;
using WinMux.Shell.Keymap;

namespace WinMux.Shell.Tests;

/// <summary>
/// What the command palette offers, as data.
///
/// The palette listed raw action identifiers with no shortcuts, which made it a list of internal
/// names rather than a way to find out what the application can do. These assert the two things
/// that fixes: the rows read as English, and they carry the key that also invokes them.
/// </summary>
public sealed class CommandPaletteModelTests
{
    private static KeyBindingTable Bindings() => new(new KeymapConfiguration
    {
        Prefix = "Ctrl+B",
        Bindings =
        [
            new KeyBindingConfiguration("%", "split-columns"),
            new KeyBindingConfiguration("Ctrl+Shift+P", "show-palette", KeyBindingScope.Direct),
        ],
    });

    [Fact]
    public void An_action_identifier_is_shown_as_a_sentence()
    {
        Assert.Equal("Split columns", CommandPaletteModel.Humanize("split-columns"));
        Assert.Equal("New tab vertical", CommandPaletteModel.Humanize("new-tab-vertical"));
    }

    [Fact]
    public void An_acronym_is_not_sentence_cased()
    {
        // "Show cwd reporting" reads as a typo; the word is not a word.
        Assert.Equal("Show CWD reporting", CommandPaletteModel.Humanize("show-cwd-reporting"));
    }

    [Fact]
    public void A_prefixed_binding_is_shown_as_both_of_its_gestures()
    {
        // "%" alone does not split a pane, so showing only "%" would be actively misleading.
        var command = CommandPaletteModel.Build(["split-columns"], Bindings()).Single();

        Assert.Equal("Ctrl+B then %", command.Shortcut);
    }

    [Fact]
    public void A_direct_binding_is_shown_alone()
    {
        var command = CommandPaletteModel.Build(["show-palette"], Bindings()).Single();

        Assert.Equal("Ctrl+Shift+P", command.Shortcut);
    }

    [Fact]
    public void An_action_with_no_binding_shows_no_shortcut()
    {
        var command = CommandPaletteModel.Build(["move-tabs-left"], Bindings()).Single();

        Assert.Equal("", command.Shortcut);
        Assert.Equal("Move tabs left", command.Label);
    }

    [Fact]
    public void Commands_are_listed_in_alphabetical_order_of_what_is_read()
    {
        var commands = CommandPaletteModel.Build(["zoom-pane", "attach-window", "split-rows"]);

        Assert.Equal(["Attach window", "Split rows", "Zoom pane"], commands.Select(c => c.Label));
    }

    [Fact]
    public void An_empty_query_matches_everything()
    {
        var commands = CommandPaletteModel.Build(["split-columns", "close-pane"]);

        Assert.Equal(commands.Count, CommandPaletteModel.Filter(commands, "   ").Count);
        Assert.Equal(commands.Count, CommandPaletteModel.Filter(commands, null).Count);
    }

    [Fact]
    public void A_prefix_match_outranks_a_match_in_the_middle_of_a_longer_name()
    {
        // Otherwise typing "split" offers "toggle-split-direction" before "split-columns".
        var commands = CommandPaletteModel.Build(["toggle-split-direction", "split-columns"]);

        var matches = CommandPaletteModel.Filter(commands, "split");

        Assert.Equal("Split columns", matches[0].Label);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void The_action_identifier_is_searchable_as_well_as_the_label()
    {
        // Someone who knows the action name should not have to guess how it was humanised.
        var commands = CommandPaletteModel.Build(["new-file-browser"]);

        Assert.Single(CommandPaletteModel.Filter(commands, "new-file"));
    }

    [Fact]
    public void A_query_that_matches_nothing_returns_nothing()
    {
        var commands = CommandPaletteModel.Build(["split-columns"]);

        Assert.Empty(CommandPaletteModel.Filter(commands, "wombat"));
    }

    [Fact]
    public void Filtering_never_loses_the_action_the_row_dispatches()
    {
        // The label is for reading; the action is what is sent. Confusing them would run the
        // wrong command, which is the worst thing a palette can do.
        var commands = CommandPaletteModel.Build(["split-columns"], Bindings());

        var match = CommandPaletteModel.Filter(commands, "Split col").Single();

        Assert.Equal("split-columns", match.Action);
    }
}

/// <summary>
/// Key gestures, written back out.
///
/// <see cref="KeyStroke.Display"/> is the inverse of <see cref="KeyStroke.Parse"/>, and the palette
/// and status bar both show its output. A gesture that does not survive the round trip is one the
/// user is being shown wrongly.
/// </summary>
public sealed class KeyStrokeDisplayTests
{
    [Theory]
    [InlineData("Ctrl+B")]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("Alt+Left")]
    [InlineData("%")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData(",")]
    [InlineData("-")]
    [InlineData("Esc")]
    [InlineData("Enter")]
    [InlineData("Space")]
    [InlineData("PageUp")]
    [InlineData("Shift+PageUp")]
    [InlineData("F5")]
    [InlineData("Ctrl+1")]
    public void A_gesture_survives_being_written_and_read_back(string gesture)
    {
        var stroke = KeyStroke.Parse(gesture);

        Assert.Equal(stroke, KeyStroke.Parse(stroke.Display()));
    }

    [Fact]
    public void A_shift_symbol_is_shown_as_the_symbol_not_as_shift_plus_a_digit()
    {
        // The binding was written "%", and the keycap says "%".
        Assert.Equal("%", new KeyStroke(Key.D5, KeyModifiers.Shift).Display());
    }

    [Fact]
    public void Modifiers_are_shown_in_the_order_Windows_writes_them()
    {
        Assert.Equal(
            "Ctrl+Alt+Shift+T",
            new KeyStroke(Key.T, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift).Display());
    }

    [Fact]
    public void A_digit_is_shown_without_its_enum_prefix()
    {
        Assert.Equal("1", new KeyStroke(Key.D1).Display());
    }
}
