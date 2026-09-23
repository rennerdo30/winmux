using System.Text;
using Avalonia.Input;

namespace WinMux.Shell;

/// <summary>
/// What a key press or a paste becomes on the wire to the program in a terminal pane.
///
/// Pure, so it can be tested without a window. The rules are xterm's, which is what every program
/// written for a terminal expects — Claude Code included: Alt+V is how it pastes an image on Windows,
/// Shift+Tab how it changes mode, and a multi-line paste must arrive bracketed or each line runs as
/// it lands.
/// </summary>
internal static class TerminalInput
{
    private const byte Escape = 0x1b;

    /// <summary>
    /// The bytes for a key, or null when the key is not one this encodes — printable characters
    /// arrive separately as text input, and are sent from there.
    /// </summary>
    /// <param name="symbol">
    /// The character the key produces on the user's own layout, as the platform reports it. Used for
    /// Alt combinations, so Alt+V sends "v" on any keyboard rather than whatever a US layout would.
    /// </param>
    /// <param name="applicationCursor">The program asked for DECCKM cursor keys.</param>
    public static byte[]? EncodeKey(Key key, KeyModifiers modifiers, string? symbol, bool applicationCursor)
    {
        var control = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        // Ctrl+Alt is AltGr on European layouts — the way to type @ or € — and belongs to text input.
        if (control && alt) return null;

        // A modifier parameter for the CSI forms: 1 + Shift + 2·Alt + 4·Ctrl, sent only when not 1.
        var parameter = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);

        switch (key)
        {
            case Key.Up: return Cursor('A', parameter, applicationCursor);
            case Key.Down: return Cursor('B', parameter, applicationCursor);
            case Key.Right: return Cursor('C', parameter, applicationCursor);
            case Key.Left: return Cursor('D', parameter, applicationCursor);
            case Key.Home: return Cursor('H', parameter, applicationCursor);
            case Key.End: return Cursor('F', parameter, applicationCursor);
            case Key.Insert: return Tilde(2, parameter);
            case Key.Delete: return Tilde(3, parameter);
            case Key.PageUp: return Tilde(5, parameter);
            case Key.PageDown: return Tilde(6, parameter);
            case Key.F1: return Function('P', parameter);
            case Key.F2: return Function('Q', parameter);
            case Key.F3: return Function('R', parameter);
            case Key.F4: return Function('S', parameter);
            case Key.F5: return Tilde(15, parameter);
            case Key.F6: return Tilde(17, parameter);
            case Key.F7: return Tilde(18, parameter);
            case Key.F8: return Tilde(19, parameter);
            case Key.F9: return Tilde(20, parameter);
            case Key.F10: return Tilde(21, parameter);
            case Key.F11: return Tilde(23, parameter);
            case Key.F12: return Tilde(24, parameter);
            case Key.Tab: return shift ? "\u001b[Z"u8.ToArray() : AltPrefixed(alt, (byte)'\t');
            case Key.Enter: return AltPrefixed(alt, (byte)'\r');
            case Key.Back: return AltPrefixed(alt, control ? (byte)0x08 : (byte)0x7f);
            case Key.Escape: return AltPrefixed(alt, Escape);
            case Key.Space when control: return [0x00];
        }

        if (control && key is >= Key.A and <= Key.Z)
        {
            return [(byte)(key - Key.A + 1)];
        }

        if (alt && !control)
        {
            // Alt+key is ESC followed by the key's own character: how every terminal program reads
            // Meta. Without this, Alt combinations sent nothing at all.
            var text = symbol is { Length: > 0 } && !char.IsControl(symbol[0])
                ? symbol
                : key switch
                {
                    >= Key.A and <= Key.Z => ((char)((shift ? 'A' : 'a') + (key - Key.A))).ToString(),
                    >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
                    _ => null,
                };

            if (text is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var result = new byte[bytes.Length + 1];
                result[0] = Escape;
                bytes.CopyTo(result, 1);
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Text from the clipboard as the program should receive it.
    ///
    /// Line breaks become carriage returns, which is what the Enter key sends — a pasted CRLF would
    /// otherwise arrive as a line break and an extra one. When the program asked for bracketed paste
    /// the text is wrapped in <c>ESC [ 200 ~</c> … <c>ESC [ 201 ~</c>, and every ESC inside it is
    /// removed: a pasted "201~" would otherwise end the paste early and run whatever follows as typed
    /// commands, which is how paste-injection attacks work.
    /// </summary>
    public static byte[] EncodePaste(string text, bool bracketed)
    {
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (!bracketed) return Encoding.UTF8.GetBytes(normalized);

        var safe = normalized.Replace("\u001b", string.Empty, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes("\u001b[200~" + safe + "\u001b[201~");
    }

    private static byte[] Cursor(char final, int parameter, bool applicationCursor) =>
        parameter != 1
            ? Encoding.ASCII.GetBytes($"\u001b[1;{parameter}{final}")
            : applicationCursor
                ? [Escape, (byte)'O', (byte)final]
                : [Escape, (byte)'[', (byte)final];

    private static byte[] Tilde(int code, int parameter) =>
        Encoding.ASCII.GetBytes(parameter == 1 ? $"\u001b[{code}~" : $"\u001b[{code};{parameter}~");

    private static byte[] Function(char final, int parameter) =>
        parameter == 1
            ? [Escape, (byte)'O', (byte)final]
            : Encoding.ASCII.GetBytes($"\u001b[1;{parameter}{final}");

    private static byte[] AltPrefixed(bool alt, byte value) => alt ? [Escape, value] : [value];
}
