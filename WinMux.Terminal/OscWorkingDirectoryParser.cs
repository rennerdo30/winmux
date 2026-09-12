using System.Text;

namespace WinMux.Terminal;

/// <summary>
/// Observes the raw terminal byte stream for shell working-directory reports. It deliberately
/// does not remove sequences from the stream; the terminal emulator remains the owner of normal
/// VT processing.
/// </summary>
internal sealed class OscWorkingDirectoryParser
{
    // Well beyond ordinary cwd reports while keeping an unterminated or hostile OSC bounded.
    private const int MaximumPayloadBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly byte[] payload = new byte[MaximumPayloadBytes];
    private ParserState state;
    private int payloadLength;
    private int utf8ContinuationBytes;

    public void Write(ReadOnlySpan<byte> bytes, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (var value in bytes)
        {
            switch (state)
            {
                case ParserState.Text:
                    if (value == Control.Escape)
                    {
                        state = ParserState.Escape;
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
        var path = ParsePayload(payload.AsSpan(0, payloadLength));
        Reset();

        if (path is not null)
        {
            report(path);
        }
    }

    private void Reset()
    {
        payloadLength = 0;
        utf8ContinuationBytes = 0;
        state = ParserState.Text;
    }

    private static string? ParsePayload(ReadOnlySpan<byte> bytes)
    {
        string value;
        try
        {
            value = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

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
