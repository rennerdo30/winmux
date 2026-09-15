namespace WinMux.Shell.Keymap;

/// <summary>
/// Immutable, validated key binding table built from user configuration.
/// </summary>
public sealed class KeyBindingTable
{
    private readonly Dictionary<KeyStroke, string> _prefixed = [];
    private readonly Dictionary<KeyStroke, string> _direct = [];

    /// <summary>
    /// Bindings written as a character rather than a key, matched against what the keyboard
    /// actually produced. See <see cref="KeyStroke.TryGetSymbol"/> for why they cannot be matched
    /// by key: "%" and ":" are not on the same keys on every layout, and the parsed stroke encodes
    /// a US keyboard's answer.
    /// </summary>
    private readonly Dictionary<string, string> _symbolPrefixed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _symbolDirect = new(StringComparer.Ordinal);

    public KeyStroke? Prefix { get; }
    public IReadOnlyList<KeyBinding> Bindings { get; }

    public KeyBindingTable(KeymapConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Prefix = string.IsNullOrWhiteSpace(configuration.Prefix)
            ? null
            : KeyStroke.Parse(configuration.Prefix).Normalized();

        var parsed = new List<KeyBinding>(configuration.Bindings.Count);
        foreach (var item in configuration.Bindings)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Action);

            var gesture = KeyStroke.Parse(item.Gesture).Normalized();
            var action = item.Action.Trim();
            var target = item.Scope == KeyBindingScope.Direct ? _direct : _prefixed;

            if (!target.TryAdd(gesture, action))
            {
                throw new ArgumentException(
                    $"Key gesture '{item.Gesture}' is bound more than once in the {item.Scope.ToString().ToLowerInvariant()} keymap.",
                    nameof(configuration));
            }

            if (KeyStroke.TryGetSymbol(item.Gesture, out var symbol))
            {
                var symbols = item.Scope == KeyBindingScope.Direct ? _symbolDirect : _symbolPrefixed;
                symbols[symbol] = action;
            }

            parsed.Add(new KeyBinding(gesture, action, item.Scope));
        }

        if (Prefix is { } prefix && _direct.ContainsKey(prefix))
        {
            throw new ArgumentException(
                $"The prefix gesture '{configuration.Prefix}' is also configured as a direct binding.",
                nameof(configuration));
        }

        if (Prefix is null && _prefixed.Count > 0)
        {
            throw new ArgumentException(
                "Prefixed bindings require a non-empty prefix gesture. Use direct bindings for a no-prefix keymap.",
                nameof(configuration));
        }

        Bindings = parsed;
    }

    public bool TryGetDirect(KeyStroke gesture, out string actionName) =>
        _direct.TryGetValue(gesture.Normalized(), out actionName!);

    public bool TryGetPrefixed(KeyStroke gesture, out string actionName) =>
        _prefixed.TryGetValue(gesture.Normalized(), out actionName!);

    /// <summary>Match a produced character against the bindings written as characters.</summary>
    public bool TryGetDirectSymbol(string? symbol, out string actionName)
    {
        actionName = string.Empty;
        return !string.IsNullOrEmpty(symbol) && _symbolDirect.TryGetValue(symbol, out actionName!);
    }

    public bool TryGetPrefixedSymbol(string? symbol, out string actionName)
    {
        actionName = string.Empty;
        return !string.IsNullOrEmpty(symbol) && _symbolPrefixed.TryGetValue(symbol, out actionName!);
    }
}
