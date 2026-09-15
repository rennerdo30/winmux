using WinMux.Shell.Keymap;

namespace WinMux.Shell;

/// <summary>One row in the command palette.</summary>
/// <param name="Action">The named action to dispatch. The only part the rest of the shell knows.</param>
/// <param name="Label">What the row reads as.</param>
/// <param name="Shortcut">The key that also does it, or empty when nothing is bound.</param>
public sealed record PaletteCommand(string Action, string Label, string Shortcut);

/// <summary>
/// What the command palette shows, as data.
///
/// Separated from the window because it is the whole of the behaviour and none of the drawing, and
/// because the palette was listing raw action identifiers — <c>split-columns</c>, <c>new-tab-vertical</c>
/// — which is the internal name of a thing, not the name of a thing. A palette is the surface where
/// a user who does not know the keymap finds out what the application can do (CLAUDE.md section 6),
/// so it has to read as English and it has to teach the shortcut.
/// </summary>
public static class CommandPaletteModel
{
    /// <summary>Words the humaniser must not sentence-case, because they are not words.</summary>
    private static readonly IReadOnlyDictionary<string, string> Acronyms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cwd"] = "CWD",
            ["cli"] = "CLI",
        };

    /// <summary>Every action, labelled, with the key that also invokes it.</summary>
    public static IReadOnlyList<PaletteCommand> Build(
        IEnumerable<string> actions,
        KeyBindingTable? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(actions);

        return actions
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => new PaletteCommand(action, Humanize(action), ShortcutFor(action, bindings)))
            .OrderBy(command => command.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The rows matching <paramref name="query"/>, best first.
    ///
    /// Matching is on the label *and* the action name, so someone who knows the action still finds
    /// it by typing the identifier, and a rank puts a prefix match above a match buried in the
    /// middle of a longer name — otherwise typing "split" offers "toggle-split-direction" first.
    /// </summary>
    public static IReadOnlyList<PaletteCommand> Filter(
        IReadOnlyList<PaletteCommand> commands,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length == 0) return commands;

        return commands
            .Select(command => (Command: command, Rank: Rank(command, trimmed)))
            .Where(scored => scored.Rank < int.MaxValue)
            .OrderBy(scored => scored.Rank)
            .ThenBy(scored => scored.Command.Label, StringComparer.OrdinalIgnoreCase)
            .Select(scored => scored.Command)
            .ToArray();
    }

    private static int Rank(PaletteCommand command, string query)
    {
        var label = command.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var action = command.Action.IndexOf(query, StringComparison.OrdinalIgnoreCase);

        if (label == 0 || action == 0) return 0;
        if (label > 0) return 1;
        if (action > 0) return 2;
        return int.MaxValue;
    }

    /// <summary>"split-columns" becomes "Split columns".</summary>
    public static string Humanize(string action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var words = action.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return action;

        var rendered = words.Select(word =>
            Acronyms.TryGetValue(word, out var acronym) ? acronym : word.ToLowerInvariant());

        var joined = string.Join(' ', rendered);
        return char.IsLower(joined[0]) ? char.ToUpperInvariant(joined[0]) + joined[1..] : joined;
    }

    private static string ShortcutFor(string action, KeyBindingTable? bindings)
    {
        if (bindings is null) return "";

        var binding = bindings.Bindings.FirstOrDefault(b =>
            string.Equals(b.ActionName, action, StringComparison.OrdinalIgnoreCase));
        if (binding is null) return "";

        // A prefixed binding is two gestures in sequence, and showing only the second would be
        // actively wrong: "%" alone does not split a pane, "Ctrl+B then %" does.
        return binding.Scope == KeyBindingScope.Prefixed && bindings.Prefix is { } prefix
            ? $"{prefix.Display()} then {binding.Gesture.Display()}"
            : binding.Gesture.Display();
    }
}
