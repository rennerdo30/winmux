using System.Text;

namespace WinMux.Terminal;

/// <summary>
/// Observes the raw terminal byte stream for the sequences WinMux acts on itself: shell
/// working-directory reports (OSC 7, OSC 9;9) and requests for the user's attention (OSC 9, 777
/// and 99, and a bare BEL). It deliberately does not remove sequences from the stream; the terminal
/// emulator remains the owner of normal VT processing.
/// </summary>
internal sealed class OscObserver
{
    /// <summary>Longest notification text passed on; a message is a sentence, not a log.</summary>
    internal const int MaximumNotificationLength = 500;

    /// <summary>Kitty notifications split across several sequences, by id. Bounded, see <see cref="Kitty"/>.</summary>
    private readonly Dictionary<string, (string? Title, string Body)> kittyParts = new(StringComparer.Ordinal);

    // Large enough for an OSC 52 clipboard write of a long passage (base64 costs a third again),
    // while keeping an unterminated or hostile OSC bounded. The buffer starts small and grows.
    internal const int MaximumPayloadBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private byte[] payload = new byte[16 * 1024];
    private ParserState state;
    private int payloadLength;
    private int utf8ContinuationBytes;

    private Action<TerminalNotification>? notify;
    private Action<string>? clipboard;

    public void Write(
        ReadOnlySpan<byte> bytes,
        Action<string> report,
        Action<TerminalNotification>? notify = null,
        Action<string>? clipboard = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        this.notify = notify;
        this.clipboard = clipboard;

        foreach (var value in bytes)
        {
            switch (state)
            {
                case ParserState.Text:
                    if (value == Control.Escape)
                    {
                        state = ParserState.Escape;
                    }
                    else if (value == Control.Bell)
                    {
                        // Outside any string sequence a BEL is the bell itself. Inside an OSC it is
                        // the terminator, which is handled by the OSC states and never reaches here.
                        notify?.Invoke(new TerminalNotification(TerminalNotificationKind.Bell, null, string.Empty));
                    }
                    break;

                case ParserState.Escape:
                    if (value == (byte)']')
                    {
                        BeginOsc();
                    }
                    else
                    {
                        state = value == Control.Escape ? ParserState.Escape : ParserState.Text;
                    }

                    break;

                case ParserState.Osc:
                    ReadOscByte(value, report);
                    break;

                case ParserState.OscEscape:
                    ReadEscapedOscByte(value, report);
                    break;

                case ParserState.DiscardOsc:
                    ReadDiscardedByte(value);
                    break;

                case ParserState.DiscardOscEscape:
                    ReadDiscardedEscapedByte(value);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown OSC parser state: {state}.");
            }
        }
    }

    private void ReadOscByte(byte value, Action<string> report)
    {
        switch (value)
        {
            case Control.Bell:
                Complete(report);
                break;
            case Control.StringTerminator:
                if (utf8ContinuationBytes == 0)
                {
                    Complete(report);
                }
                else
                {
                    Append(value);
                }

                break;
            case Control.Escape:
                state = ParserState.OscEscape;
                break;
            case Control.Cancel:
            case Control.Substitute:
                Reset();
                break;
            default:
                Append(value);
                break;
        }
    }

    private void ReadEscapedOscByte(byte value, Action<string> report)
    {
        if (value == (byte)'\\')
        {
            Complete(report);
            return;
        }

        if (value == Control.Bell || value == Control.StringTerminator)
        {
            Complete(report);
            return;
        }

        if (value == Control.Cancel || value == Control.Substitute)
        {
            Reset();
            return;
        }

        Append(Control.Escape);
        if (state == ParserState.DiscardOsc)
        {
            ReadDiscardedByte(value);
        }
        else if (value == Control.Escape)
        {
            state = ParserState.OscEscape;
        }
        else
        {
            Append(value);
        }
    }

    private void ReadDiscardedByte(byte value)
    {
        if (value == Control.Bell || value == Control.Cancel || value == Control.Substitute)
        {
            Reset();
        }
        else if (value == Control.StringTerminator)
        {
            if (utf8ContinuationBytes == 0)
            {
                Reset();
            }
            else
            {
                TrackUtf8(value);
            }
        }
        else if (value == Control.Escape)
        {
            utf8ContinuationBytes = 0;
            state = ParserState.DiscardOscEscape;
        }
        else
        {
            TrackUtf8(value);
        }
    }

    private void ReadDiscardedEscapedByte(byte value)
    {
        if (value == (byte)'\\' || value == Control.Bell ||
            value == Control.StringTerminator || value == Control.Cancel ||
            value == Control.Substitute)
        {
            Reset();
        }
        else
        {
            TrackUtf8(value);
            state = value == Control.Escape
                ? ParserState.DiscardOscEscape
                : ParserState.DiscardOsc;
        }
    }

    private void BeginOsc()
    {
        payloadLength = 0;
        utf8ContinuationBytes = 0;
        state = ParserState.Osc;
    }

    private void Append(byte value)
    {
        if (payloadLength == payload.Length && payload.Length < MaximumPayloadBytes)
        {
            Array.Resize(ref payload, Math.Min(payload.Length * 2, MaximumPayloadBytes));
        }

        if (payloadLength == payload.Length)
        {
            payloadLength = 0;
            TrackUtf8(value);
            state = ParserState.DiscardOsc;
            return;
        }

        payload[payloadLength++] = value;
        TrackUtf8(value);
        state = ParserState.Osc;
    }

    private void TrackUtf8(byte value)
    {
        if (utf8ContinuationBytes > 0)
        {
            if (value is >= 0x80 and <= 0xbf)
            {
                utf8ContinuationBytes--;
                return;
            }

            utf8ContinuationBytes = 0;
        }

        utf8ContinuationBytes = value switch
        {
            >= 0xc2 and <= 0xdf => 1,
            >= 0xe0 and <= 0xef => 2,
            >= 0xf0 and <= 0xf4 => 3,
            _ => 0,
        };
    }

    private void Complete(Action<string> report)
    {
        var text = Decode(payload.AsSpan(0, payloadLength));
        Reset();
        if (text is null) return;

        if (ParsePayload(text) is { } path)
        {
            report(path);
            return;
        }

        if (clipboard is not null && text.StartsWith("52;", StringComparison.Ordinal))
        {
            if (ParseClipboardWrite(text) is { } copied) clipboard(copied);
            return;
        }

        if (notify is not null && ParseNotification(text) is { } notification)
        {
            notify(notification);
        }
    }

    /// <summary>
    /// OSC 52 — <c>52 ; selection ; base64</c> — a program putting text on the clipboard: how Claude
    /// Code, vim and tmux copy from inside a terminal. Only writing is honoured. A payload of
    /// <c>?</c> asks the terminal to send the clipboard back, and answering would let any program
    /// in a pane — or anything it prints, a file shown with cat — read what the user last copied.
    /// </summary>
    private static string? ParseClipboardWrite(string value)
    {
        var rest = value[3..];
        var split = rest.IndexOf(';');
        if (split < 0) return null;

        var data = rest[(split + 1)..];
        if (data.Length == 0 || data == "?") return null;

        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(data));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// The attention requests. OSC 9 is shared with ConEmu, whose numbered subcommands — 9;9 is the
    /// working directory, 9;4 a progress bar — are not messages; iTerm2's message is free text.
    /// </summary>
    private TerminalNotification? ParseNotification(string value)
    {
        if (value.StartsWith("9;", StringComparison.Ordinal))
        {
            var message = value[2..];
            if (IsConEmuSubcommand(message)) return null;
            return Make(TerminalNotificationKind.Osc9, null, message);
        }

        if (value.StartsWith("777;notify;", StringComparison.Ordinal))
        {
            // title;body — and the body may itself contain semicolons.
            var rest = value["777;notify;".Length..];
            var split = rest.IndexOf(';');
            return split < 0
                ? Make(TerminalNotificationKind.Osc777, null, rest)
                : Make(TerminalNotificationKind.Osc777, rest[..split], rest[(split + 1)..]);
        }

        if (value.StartsWith("99;", StringComparison.Ordinal))
        {
            return Kitty(value[3..]);
        }

        return null;
    }

    private static bool IsConEmuSubcommand(string message)
    {
        var index = 0;
        while (index < message.Length && char.IsAsciiDigit(message[index])) index++;
        return index > 0 && (index == message.Length || message[index] == ';');
    }

    /// <summary>
    /// Kitty's OSC 99: <c>metadata ; payload</c>, where metadata is <c>key=value</c> pairs joined by
    /// colons. <c>p</c> says whether the payload is the title or the body, <c>d=0</c> that more parts
    /// follow under the same <c>i</c>, and <c>e=1</c> that the payload is base64. Only what a desktop
    /// notification needs is read; actions, icons and sounds are ignored.
    /// </summary>
    private TerminalNotification? Kitty(string value)
    {
        var split = value.IndexOf(';');
        if (split < 0) return null;

        var metadata = value[..split];
        var text = value[(split + 1)..];
        string id = string.Empty, part = "title";
        var done = true;
        var encoded = false;

        foreach (var pair in metadata.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            var key = pair[..equals];
            var setting = pair[(equals + 1)..];
            switch (key)
            {
                case "i": id = setting; break;
                case "p": part = setting; break;
                case "d": done = setting != "0"; break;
                case "e": encoded = setting == "1"; break;
            }
        }

        if (part is not ("title" or "body")) return null;

        if (encoded)
        {
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        kittyParts.TryGetValue(id, out var so);
        var title = part == "title" ? (so.Title ?? string.Empty) + text : so.Title;
        var body = part == "body" ? (so.Body ?? string.Empty) + text : so.Body ?? string.Empty;

        if (!done)
        {
            // A program that opens notifications and never finishes them must not grow this without
            // limit; the oldest unfinished one is simply dropped.
            if (!kittyParts.ContainsKey(id) && kittyParts.Count >= 8) kittyParts.Remove(kittyParts.Keys.First());
            kittyParts[id] = (title, body);
            return null;
        }

        kittyParts.Remove(id);

        // A title with no body is still a notification; show the title as the message.
        return string.IsNullOrWhiteSpace(body)
            ? Make(TerminalNotificationKind.Osc99, null, title ?? string.Empty)
            : Make(TerminalNotificationKind.Osc99, title, body);
    }

    private static TerminalNotification? Make(TerminalNotificationKind kind, string? title, string body)
    {
        var cleanBody = Clean(body);
        var cleanTitle = title is null ? null : Clean(title);
        if (cleanBody.Length == 0 && string.IsNullOrEmpty(cleanTitle)) return null;
        return new TerminalNotification(kind, string.IsNullOrEmpty(cleanTitle) ? null : cleanTitle, cleanBody);
    }

    /// <summary>No control characters, collapsed whitespace, and a length a notification can show.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaximumNotificationLength));
        var space = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space) builder.Append(' ');
            space = false;
            builder.Append(character);
            if (builder.Length >= MaximumNotificationLength)
            {
                builder.Append('…');
                break;
            }
        }

        return builder.ToString();
    }

    private void Reset()
    {
        payloadLength = 0;
        utf8ContinuationBytes = 0;
        state = ParserState.Text;
    }

    private static string? ParsePayload(string value)
    {
        if (value.StartsWith("9;9;", StringComparison.Ordinal))
        {
            return ValidatePath(Unquote(value[4..]));
        }

        return value.StartsWith("7;", StringComparison.Ordinal)
            ? ParseFileUrl(value[2..])
            : null;
    }

    private static string? ParseFileUrl(string value)
    {
        const string prefix = "file://";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var authorityAndPath = value.AsSpan(prefix.Length);
        var slash = authorityAndPath.IndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        var authority = authorityAndPath[..slash];
        var encodedPath = authorityAndPath[slash..];

        // Tolerate the common but non-canonical file://C:/path spelling.
        if (authority.Length == 2 && IsAsciiLetter(authority[0]) && authority[1] == ':')
        {
            encodedPath = string.Concat(authority, encodedPath).AsSpan();
        }

        var path = PercentDecode(encodedPath);
        if (path is null)
        {
            return null;
        }

        // RFC 8089 represents drive-letter paths as /C:/path. Do not otherwise normalize
        // separators: /home/user is a WSL/POSIX path and must remain one.
        if (path.Length >= 3 && path[0] == '/' && IsAsciiLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }

        return ValidatePath(path);
    }

    private static string? PercentDecode(ReadOnlySpan<char> value)
    {
        var encoded = Encoding.UTF8.GetBytes(value.ToString());
        var decoded = new byte[encoded.Length];
        var decodedLength = 0;

        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != (byte)'%')
            {
                decoded[decodedLength++] = encoded[index];
                continue;
            }

            if (index + 2 >= encoded.Length ||
                !TryParseHex(encoded[index + 1], out var high) ||
                !TryParseHex(encoded[index + 2], out var low))
            {
                return null;
            }

            decoded[decodedLength++] = (byte)((high << 4) | low);
            index += 2;
        }

        try
        {
            return StrictUtf8.GetString(decoded, 0, decodedLength);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private static string? ValidatePath(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (!IsAbsolutePath(value))
        {
            return null;
        }

        foreach (var character in value)
        {
            if (character < ' ' || character == '\x7f')
            {
                return null;
            }
        }

        return value;
    }

    private static bool IsAbsolutePath(string value) =>
        value[0] == '/' ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        (value.Length >= 3 && IsAsciiLetter(value[0]) && value[1] == ':' &&
         value[2] is '/' or '\\');

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool TryParseHex(byte value, out int parsed)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            parsed = value - '0';
            return true;
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            parsed = value - 'A' + 10;
            return true;
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            parsed = value - 'a' + 10;
            return true;
        }

        parsed = 0;
        return false;
    }

    private enum ParserState
    {
        Text,
        Escape,
        Osc,
        OscEscape,
        DiscardOsc,
        DiscardOscEscape,
    }

    private static class Control
    {
        public const byte Bell = 0x07;
        public const byte Cancel = 0x18;
        public const byte Substitute = 0x1a;
        public const byte Escape = 0x1b;
        public const byte StringTerminator = 0x9c;
    }
}
