namespace WinMux.Shell.Keymap;

public enum KeyBindingScope
{
    Prefixed,
    Direct,
}

/// <summary>A parsed entry in the shell's one binding table.</summary>
public sealed record KeyBinding(KeyStroke Gesture, string ActionName, KeyBindingScope Scope);

/// <summary>
/// Serializable configuration shape. Gesture examples: Ctrl+B, %, Shift+Left, Alt+T.
/// </summary>
public sealed record KeyBindingConfiguration(
    string Gesture,
    string Action,
    KeyBindingScope Scope = KeyBindingScope.Prefixed);
