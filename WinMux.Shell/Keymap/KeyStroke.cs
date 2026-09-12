using Avalonia.Input;

namespace WinMux.Shell.Keymap;

/// <summary>
/// Framework-neutral-in-practice representation of an Avalonia key event. Matching is exact:
/// Ctrl+X does not accidentally also match Ctrl+Shift+X.
/// </summary>
public readonly record struct KeyStroke(Key Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    private const KeyModifiers SupportedModifiers =
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;

    private static readonly IReadOnlyDictionary<string, KeyStroke> SymbolKeys =
        new Dictionary<string, KeyStroke>(StringComparer.OrdinalIgnoreCase)
        {
            ["%"] = new(Key.D5, KeyModifiers.Shift),
            ["\""] = new(Key.OemQuotes, KeyModifiers.Shift),
            ["'"] = new(Key.OemQuotes),
            ["+"] = new(Key.OemPlus, KeyModifiers.Shift),
            ["="] = new(Key.OemPlus),
            ["-"] = new(Key.OemMinus),
            ["_"] = new(Key.OemMinus, KeyModifiers.Shift),
            [","] = new(Key.OemComma),
            ["<"] = new(Key.OemComma, KeyModifiers.Shift),
            ["."] = new(Key.OemPeriod),
            [">"] = new(Key.OemPeriod, KeyModifiers.Shift),
            ["/"] = new(Key.OemQuestion),
            ["?"] = new(Key.OemQuestion, KeyModifiers.Shift),
            [";"] = new(Key.OemSemicolon),
            [":"] = new(Key.OemSemicolon, KeyModifiers.Shift),
            ["["] = new(Key.OemOpenBrackets),
            ["{"] = new(Key.OemOpenBrackets, KeyModifiers.Shift),
            ["]"] = new(Key.OemCloseBrackets),
            ["}"] = new(Key.OemCloseBrackets, KeyModifiers.Shift),
            ["\\"] = new(Key.OemPipe),
            ["|"] = new(Key.OemPipe, KeyModifiers.Shift),
            ["`"] = new(Key.OemTilde),
            ["~"] = new(Key.OemTilde, KeyModifiers.Shift),
        };

    private static readonly IReadOnlyDictionary<string, Key> KeyAliases =
        new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            ["Esc"] = Key.Escape,
            ["Enter"] = Key.Enter,
            ["Return"] = Key.Enter,
            ["Space"] = Key.Space,
            ["PageUp"] = Key.PageUp,
            ["PgUp"] = Key.PageUp,
            ["PageDown"] = Key.PageDown,
            ["PgDown"] = Key.PageDown,
            ["Backspace"] = Key.Back,
            ["Delete"] = Key.Delete,
            ["Del"] = Key.Delete,
            ["Insert"] = Key.Insert,
            ["Ins"] = Key.Insert,
        };

    public KeyStroke Normalized() => new(Key, Modifiers & SupportedModifiers);

    public static KeyStroke Parse(string gesture)
    {
        if (!TryParse(gesture, out var stroke, out var error))
            throw new FormatException(error);
        return stroke;
    }

    public static bool TryParse(string? gesture, out KeyStroke stroke) =>
        TryParse(gesture, out stroke, out _);

    public static bool TryParse(string? gesture, out KeyStroke stroke, out string? error)
    {
        stroke = default;
        error = null;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            error = "A key gesture cannot be empty.";
            return false;
        }

        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = $"Key gesture '{gesture}' has no key.";
            return false;
        }

        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!TryParseModifier(parts[i], out var modifier))
            {
                error = $"Unknown modifier '{parts[i]}' in key gesture '{gesture}'.";
                return false;
            }

            if ((modifiers & modifier) != 0)
            {
                error = $"Modifier '{parts[i]}' appears more than once in key gesture '{gesture}'.";
                return false;
            }
            modifiers |= modifier;
        }

        var keyToken = parts[^1];
        if (SymbolKeys.TryGetValue(keyToken, out var symbol))
        {
            stroke = new KeyStroke(symbol.Key, modifiers | symbol.Modifiers).Normalized();
            return true;
        }

        if (KeyAliases.TryGetValue(keyToken, out var alias))
        {
            stroke = new KeyStroke(alias, modifiers).Normalized();
            return true;
        }

        if (keyToken.Length == 1 && char.IsDigit(keyToken[0]))
            keyToken = "D" + keyToken;

        if (!Enum.TryParse<Key>(keyToken, ignoreCase: true, out var key) || key == Key.None)
        {
            error = $"Unknown key '{parts[^1]}' in key gesture '{gesture}'.";
            return false;
        }

        stroke = new KeyStroke(key, modifiers).Normalized();
        return true;
    }

    private static bool TryParseModifier(string value, out KeyModifiers modifier)
    {
        modifier = value.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => KeyModifiers.Control,
            "ALT" => KeyModifiers.Alt,
            "SHIFT" => KeyModifiers.Shift,
            "META" or "WIN" or "WINDOWS" => KeyModifiers.Meta,
            _ => KeyModifiers.None,
        };
        return modifier != KeyModifiers.None;
    }
}
