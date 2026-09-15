using Avalonia.Input;
using WinMux.Shell.Actions;

namespace WinMux.Shell.Keymap;

public enum KeymapRouteKind
{
    PassThrough,
    PrefixArmed,
    ActionDispatched,
    UnboundPrefixedKey,
}

public sealed record KeymapRouteResult(
    KeymapRouteKind Kind,
    string? ActionName = null,
    ActionDispatchResult? Dispatch = null)
{
    /// <summary>Whether the shell should set Avalonia's KeyEventArgs.Handled.</summary>
    public bool Handled => Kind != KeymapRouteKind.PassThrough;
}

/// <summary>
/// Stateful bridge from key events to the shared named-action dispatcher.
/// </summary>
public sealed class KeymapRouter(KeyBindingTable bindings, ActionDispatcher dispatcher)
{
    private readonly KeyBindingTable _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    private readonly ActionDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public bool IsPrefixArmed { get; private set; }

    public KeymapRouteResult Route(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        Route(new KeyStroke(key, modifiers));

    public KeymapRouteResult Route(KeyStroke gesture) => Route(gesture, symbol: null);

    /// <param name="symbol">
    /// The character the key produced on this keyboard, from <c>KeyEventArgs.KeySymbol</c>. Only
    /// used when the physical key matched nothing, so an explicit key binding always wins.
    /// </param>
    public KeymapRouteResult Route(KeyStroke gesture, string? symbol)
    {
        gesture = gesture.Normalized();

        // Only a bare character counts. Ctrl+X is about the key, and a stray Ctrl must not turn a
        // keystroke into the symbol binding for the character it happens to produce.
        var plain = (gesture.Modifiers & ~KeyModifiers.Shift) == 0 ? symbol : null;

        if (IsPrefixArmed)
        {
            IsPrefixArmed = false;
            if (_bindings.TryGetPrefixed(gesture, out var prefixedAction)) return Dispatch(prefixedAction);
            return _bindings.TryGetPrefixedSymbol(plain, out var prefixedSymbolAction)
                ? Dispatch(prefixedSymbolAction)
                : new KeymapRouteResult(KeymapRouteKind.UnboundPrefixedKey);
        }

        if (_bindings.TryGetDirect(gesture, out var directAction))
            return Dispatch(directAction);

        if (_bindings.TryGetDirectSymbol(plain, out var directSymbolAction))
            return Dispatch(directSymbolAction);

        if (_bindings.Prefix is { } prefix && prefix == gesture)
        {
            IsPrefixArmed = true;
            return new KeymapRouteResult(KeymapRouteKind.PrefixArmed);
        }

        return new KeymapRouteResult(KeymapRouteKind.PassThrough);
    }

    public void CancelPrefix() => IsPrefixArmed = false;

    private KeymapRouteResult Dispatch(string actionName) => new(
        KeymapRouteKind.ActionDispatched,
        actionName,
        _dispatcher.Dispatch(actionName));
}
