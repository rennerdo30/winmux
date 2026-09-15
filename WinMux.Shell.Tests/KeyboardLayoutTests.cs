using Avalonia.Input;
using WinMux.Shell.Actions;
using WinMux.Shell.Keymap;

namespace WinMux.Shell.Tests;

/// <summary>
/// Bindings written as characters, on keyboards that are not American.
///
/// `Ctrl+B` then `:` opens the command palette. Parsing ":" gives Shift+OemSemicolon, which is
/// where a colon lives on a US ANSI keyboard. On the Japanese 106/109 keyboard this was found on,
/// `:` is its own *unshifted* key — so the binding could never fire and the palette had no key at
/// all. The prefix armed, the next key matched nothing, and the status bar said "no binding for
/// OemSemicolon", which is true and useless. `"` was broken the same way: the table wants
/// Shift+OemQuotes, JIS puts it on Shift+2.
///
/// The fix matches those bindings against the character the keyboard actually produced
/// (<c>KeyEventArgs.KeySymbol</c>) rather than against the key that produces it in Redmond.
/// </summary>
public sealed class KeyboardLayoutTests
{
    private static KeymapRouter Router(out ActionDispatcher dispatcher, out List<string> invoked)
    {
        var log = new List<string>();
        var actions = new ActionDispatcher();
        actions.Register("show-palette", () => log.Add("show-palette"));
        actions.Register("split-columns", () => log.Add("split-columns"));
        actions.Register("close-pane", () => log.Add("close-pane"));

        var table = new KeyBindingTable(new KeymapConfiguration
        {
            Prefix = "Ctrl+B",
            Bindings =
            [
                new KeyBindingConfiguration(":", "show-palette"),
                new KeyBindingConfiguration("%", "split-columns"),
                new KeyBindingConfiguration("x", "close-pane"),
            ],
        });

        dispatcher = actions;
        invoked = log;
        return new KeymapRouter(table, actions);
    }

    /// <summary>
    /// A Japanese 106/109 keyboard's colon: the semicolon key, unshifted. Measured with
    /// <c>VkKeyScanEx</c> on the machine this bug was found on, not guessed.
    /// </summary>
    private static readonly KeyStroke JisColon = new(Key.OemSemicolon);

    /// <summary>A US ANSI keyboard's colon: Shift and the semicolon key.</summary>
    private static readonly KeyStroke UsColon = new(Key.OemSemicolon, KeyModifiers.Shift);

    [Fact]
    public void A_symbol_binding_fires_on_the_key_that_produces_it_on_a_us_keyboard()
    {
        var router = Router(out _, out var invoked);

        router.Route(KeyStroke.Parse("Ctrl+B"), null);
        var result = router.Route(UsColon, ":");

        Assert.Equal(KeymapRouteKind.ActionDispatched, result.Kind);
        Assert.Equal(["show-palette"], invoked);
    }

    [Fact]
    public void A_symbol_binding_fires_on_a_japanese_keyboard_too()
    {
        // The whole point: a different physical key, the same character, the same action.
        var router = Router(out _, out var invoked);

        router.Route(KeyStroke.Parse("Ctrl+B"), null);
        var result = router.Route(JisColon, ":");

        Assert.Equal(KeymapRouteKind.ActionDispatched, result.Kind);
        Assert.Equal(["show-palette"], invoked);
    }

    [Fact]
    public void Without_the_character_a_foreign_layout_still_matches_nothing()
    {
        // Proves the character is what fixed it, rather than something incidental.
        var router = Router(out _, out var invoked);

        router.Route(KeyStroke.Parse("Ctrl+B"), null);
        var result = router.Route(JisColon, symbol: null);

        Assert.Equal(KeymapRouteKind.UnboundPrefixedKey, result.Kind);
        Assert.Empty(invoked);
    }

    [Fact]
    public void A_key_binding_still_wins_over_a_character_that_happens_to_match()
    {
        // "x" is bound as a key. Matching by key first keeps every existing binding behaving
        // exactly as it did.
        var router = Router(out _, out var invoked);

        router.Route(KeyStroke.Parse("Ctrl+B"), null);
        router.Route(new KeyStroke(Key.X), "x");

        Assert.Equal(["close-pane"], invoked);
    }

    [Fact]
    public void A_modified_keystroke_does_not_match_a_bare_character_binding()
    {
        // Ctrl+: is not ":", and a stray modifier must not fire a binding the user did not press.
        var router = Router(out _, out var invoked);

        router.Route(KeyStroke.Parse("Ctrl+B"), null);
        var result = router.Route(new KeyStroke(Key.OemSemicolon, KeyModifiers.Control), ":");

        Assert.Equal(KeymapRouteKind.UnboundPrefixedKey, result.Kind);
        Assert.Empty(invoked);
    }

    [Fact]
    public void Shift_alone_does_not_disqualify_a_character_binding()
    {
        // Producing most symbols requires Shift, so requiring "no modifiers" would break them all.
        var router = Router(out _, out var invoked);

        router.Route(KeyStroke.Parse("Ctrl+B"), null);
        router.Route(new KeyStroke(Key.D5, KeyModifiers.Shift), "%");

        Assert.Equal(["split-columns"], invoked);
    }

    [Fact]
    public void An_unrelated_character_is_still_unbound()
    {
        var router = Router(out _, out var invoked);

        router.Route(KeyStroke.Parse("Ctrl+B"), null);
        var result = router.Route(new KeyStroke(Key.OemPlus), "+");

        Assert.Equal(KeymapRouteKind.UnboundPrefixedKey, result.Kind);
        Assert.Empty(invoked);
    }

    [Theory]
    [InlineData(":")]
    [InlineData("%")]
    [InlineData("\"")]
    public void A_gesture_written_as_a_character_is_recognised_as_one(string gesture)
    {
        Assert.True(KeyStroke.TryGetSymbol(gesture, out var symbol));
        Assert.Equal(gesture, symbol);
    }

    [Theory]
    [InlineData("Ctrl+B")]
    [InlineData("Left")]
    [InlineData("F5")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData(null)]
    public void A_gesture_written_as_a_key_is_not(string? gesture)
    {
        Assert.False(KeyStroke.TryGetSymbol(gesture, out _));
    }
}
