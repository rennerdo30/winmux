namespace WinMux.Shell.Keymap;

/// <summary>
/// Immutable, validated key binding table built from user configuration.
/// </summary>
public sealed class KeyBindingTable
{
    private readonly Dictionary<KeyStroke, string> _prefixed = [];
    private readonly Dictionary<KeyStroke, string> _direct = [];

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
}
