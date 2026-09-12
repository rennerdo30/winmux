using Avalonia.Input;
using WinMux.Shell.Actions;
using WinMux.Shell.Keymap;

namespace WinMux.Shell.Tests;

public class KeymapTests
{
    [Theory]
    [InlineData("Ctrl+B", Key.B, KeyModifiers.Control)]
    [InlineData("%", Key.D5, KeyModifiers.Shift)]
    [InlineData("\"", Key.OemQuotes, KeyModifiers.Shift)]
    [InlineData("Shift+Left", Key.Left, KeyModifiers.Shift)]
    [InlineData("Alt+1", Key.D1, KeyModifiers.Alt)]
    public void Gestures_parse_to_exact_key_strokes(string text, Key key, KeyModifiers modifiers)
    {
        Assert.Equal(new KeyStroke(key, modifiers), KeyStroke.Parse(text));
    }

    [Fact]
    public void Tmux_default_arms_prefix_then_dispatches_the_named_action()
    {
        var calls = 0;
        var dispatcher = new ActionDispatcher();
        dispatcher.Register(ShellActionNames.SplitColumns, () => calls++);
        var router = new KeymapRouter(
            new KeyBindingTable(KeymapConfiguration.TmuxDefaults()),
            dispatcher);

        var prefix = router.Route(Key.B, KeyModifiers.Control);
        var action = router.Route(Key.D5, KeyModifiers.Shift);

        Assert.Equal(KeymapRouteKind.PrefixArmed, prefix.Kind);
        Assert.True(prefix.Handled);
        Assert.Equal(KeymapRouteKind.ActionDispatched, action.Kind);
        Assert.Equal(ShellActionNames.SplitColumns, action.ActionName);
        Assert.True(action.Dispatch!.Succeeded);
        Assert.Equal(1, calls);
        Assert.False(router.IsPrefixArmed);
    }

    [Fact]
    public void Unbound_key_passes_through_to_the_terminal()
    {
        var router = Router(KeymapConfiguration.TmuxDefaults());

        var result = router.Route(Key.D);

        Assert.Equal(KeymapRouteKind.PassThrough, result.Kind);
        Assert.False(result.Handled);
    }

    [Fact]
    public void Unknown_key_after_prefix_is_consumed_and_clears_prefix_state()
    {
        var router = Router(KeymapConfiguration.TmuxDefaults());
        router.Route(Key.B, KeyModifiers.Control);

        var result = router.Route(Key.F12);

        Assert.Equal(KeymapRouteKind.UnboundPrefixedKey, result.Kind);
        Assert.True(result.Handled);
        Assert.False(router.IsPrefixArmed);
    }

    [Fact]
    public void A_direct_binding_dispatches_without_arming_the_prefix()
    {
        var calls = 0;
        var dispatcher = new ActionDispatcher();
        dispatcher.Register(ShellActionNames.NewTab, () => calls++);
        var configuration = new KeymapConfiguration
        {
            Prefix = "Ctrl+B",
            Bindings =
            [
                new("Alt+T", ShellActionNames.NewTab, KeyBindingScope.Direct),
                new("C", ShellActionNames.NewTab, KeyBindingScope.Prefixed),
            ],
        };
        var router = new KeymapRouter(new KeyBindingTable(configuration), dispatcher);

        var result = router.Route(Key.T, KeyModifiers.Alt);

        Assert.Equal(KeymapRouteKind.ActionDispatched, result.Kind);
        Assert.Equal(1, calls);
        Assert.False(router.IsPrefixArmed);
    }

    [Fact]
    public void No_prefix_defaults_make_every_action_direct()
    {
        var calls = 0;
        var dispatcher = new ActionDispatcher();
        dispatcher.Register(ShellActionNames.FocusLeft, () => calls++);
        var table = new KeyBindingTable(KeymapConfiguration.NoPrefixDefaults());
        var router = new KeymapRouter(table, dispatcher);

        var result = router.Route(Key.Left);

        Assert.Null(table.Prefix);
        Assert.Equal(KeymapRouteKind.ActionDispatched, result.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Default_table_covers_every_phase_one_action()
    {
        var table = new KeyBindingTable(KeymapConfiguration.TmuxDefaults());

        Assert.Empty(ShellActionNames.All.Except(table.Bindings.Select(x => x.ActionName)));
    }

    [Fact]
    public void Duplicate_binding_in_the_same_scope_is_rejected()
    {
        var configuration = new KeymapConfiguration
        {
            Bindings =
            [
                new("X", ShellActionNames.ClosePane),
                new("X", ShellActionNames.NewTab),
            ],
        };

        var error = Assert.Throws<ArgumentException>(() => new KeyBindingTable(configuration));
        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void Prefixed_bindings_without_a_prefix_are_rejected()
    {
        var configuration = new KeymapConfiguration
        {
            Prefix = null,
            Bindings = [new("X", ShellActionNames.ClosePane)],
        };

        var error = Assert.Throws<ArgumentException>(() => new KeyBindingTable(configuration));
        Assert.Contains("require", error.Message);
    }

    [Fact]
    public void Json_configuration_can_replace_the_default_prefix_and_bindings()
    {
        var path = Path.Combine(Path.GetTempPath(), "winmux-keymap-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {
                  "prefix": null,
                  "bindings": [
                    { "gesture": "Alt+T", "action": "new-tab", "scope": "Direct" }
                  ]
                }
                """);

            var loaded = KeymapConfiguration.Load(path);
            var table = new KeyBindingTable(loaded);

            Assert.Null(table.Prefix);
            Assert.True(table.TryGetDirect(KeyStroke.Parse("Alt+T"), out var action));
            Assert.Equal(ShellActionNames.NewTab, action);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static KeymapRouter Router(KeymapConfiguration configuration) =>
        new(new KeyBindingTable(configuration), new ActionDispatcher());
}
