namespace WinMux.Terminal;

/// <summary>
/// Rewrites colon-form SGR underline parameters into the plain form the engine understands.
///
/// <para>
/// `ESC[4:0m` means <em>underline off</em>; `4:1` single, `4:2` double, `4:3` curly. They are the
/// modern spelling, and `Terminal.Emulation` reads the sub-parameter as if it were not there — so
/// `4:0` arrives as a bare `4` and turns underline **on** when it was asked to turn it off. Nothing
/// ever clears it again, and every line drawn afterwards is underlined. That is not a hypothetical:
/// it is what a pane running Claude Code looked like, and `UnderlineSubParameterTests` pins it.
/// </para>
///
/// <para>
/// The engine is a third-party package whose source repository does not resolve
/// (<c>docs/adr/0018-terminal-emulation-supply-chain.md</c>), so there is nowhere to send a patch
/// and 0.3.4 has the same bug. This is the alternative: fix the byte stream on the way in, in the
/// adapter that owns the engine, where <c>ITerminalEngine</c> already exists to absorb exactly this
/// kind of difference.
/// </para>
///
/// <para>
/// It rewrites **only** parameters beginning <c>4:</c>, and only inside a CSI sequence ending in
/// <c>m</c>. Everything else — including `58:2::255:0:0`, which sets an underline colour and is full
/// of colons — passes through byte for byte.
/// </para>
/// </summary>
internal sealed class SgrColonNormalizer
{
    private const byte Escape = 0x1B;
    private const byte CsiFinalLow = 0x40;
    private const byte CsiFinalHigh = 0x7E;
    private const byte SelectGraphicRendition = (byte)'m';

    /// <summary>
    /// A CSI sequence longer than this is not one worth holding on to. Real SGR sequences are a few
    /// dozen bytes; the cap stops a stream that never sends a final byte from growing the buffer
    /// without limit.
    /// </summary>
    private const int MaximumSequence = 256;

    private enum State { Text, AfterEscape, InsideCsi }

    private State _state = State.Text;

    /// <summary>The CSI sequence being collected, which may have started in an earlier write.</summary>
    private readonly List<byte> _sequence = [];

    private byte[] _output = new byte[1024];
    private int _length;

    /// <summary>
    /// The input with colon-form underline parameters rewritten.
    ///
    /// The result is valid until the next call: the caller hands it straight to the engine, so
    /// reusing one buffer avoids an allocation per write on the hot path.
    /// </summary>
    public ReadOnlySpan<byte> Normalize(ReadOnlySpan<byte> input)
    {
        _length = 0;

        foreach (var value in input)
        {
            switch (_state)
            {
                case State.Text:
                    if (value == Escape)
                    {
                        _state = State.AfterEscape;
                        _sequence.Clear();
                        _sequence.Add(value);
                    }
                    else
                    {
                        Emit(value);
                    }

                    break;

                case State.AfterEscape:
                    _sequence.Add(value);
                    if (value == (byte)'[')
                    {
                        _state = State.InsideCsi;
                    }
                    else
                    {
                        // Not a CSI — OSC, a charset selection, a lone ESC. None of our business.
                        Flush();
                        _state = State.Text;
                    }

                    break;

                case State.InsideCsi:
                    _sequence.Add(value);

                    if (value is >= CsiFinalLow and <= CsiFinalHigh)
                    {
                        if (value == SelectGraphicRendition) EmitRewritten();
                        else Flush();
                        _state = State.Text;
                    }
                    else if (_sequence.Count > MaximumSequence)
                    {
                        // Malformed or hostile. Pass it on and let the engine decide; holding it
                        // would only delay the same outcome and cost memory meanwhile.
                        Flush();
                        _state = State.Text;
                    }

                    break;
            }
        }

        // A sequence still open at the end of the chunk stays open: ConPTY splits writes wherever it
        // likes, and an ESC[ can and does arrive with its parameters in the next read. Holding it is
        // the whole reason this type has state.
        return _output.AsSpan(0, _length);
    }

    /// <summary>Emit the collected CSI ... m with every <c>4:N</c> parameter rewritten.</summary>
    private void EmitRewritten()
    {
        // _sequence is ESC [ <parameters> m. Anything else has already gone through Flush.
        var parameters = _sequence.GetRange(2, _sequence.Count - 3);

        if (!NeedsRewriting(parameters))
        {
            Flush();
            return;
        }

        Emit(Escape);
        Emit((byte)'[');

        var start = 0;
        for (var index = 0; index <= parameters.Count; index++)
        {
            if (index != parameters.Count && parameters[index] != (byte)';') continue;

            EmitParameter(parameters, start, index - start);
            if (index != parameters.Count) Emit((byte)';');
            start = index + 1;
        }

        Emit(SelectGraphicRendition);
    }

    /// <summary>Whether any parameter begins <c>4:</c>. Most sequences do not, and skip the work.</summary>
    private static bool NeedsRewriting(List<byte> parameters)
    {
        for (var index = 0; index < parameters.Count - 1; index++)
        {
            var atStart = index == 0 || parameters[index - 1] == (byte)';';
            if (atStart && parameters[index] == (byte)'4' && parameters[index + 1] == (byte)':') return true;
        }

        return false;
    }

    private void EmitParameter(List<byte> parameters, int start, int count)
    {
        // Only "4:..." is rewritten; "24", "58:2::255:0:0" and the rest are copied unchanged.
        if (count >= 2 && parameters[start] == (byte)'4' && parameters[start + 1] == (byte)':')
        {
            // "4:0" is underline off, which SGR spells 24. Every other style — single, double,
            // curly, dotted, dashed — is underline on, and the engine draws one line for all of
            // them, so they all become a plain 4 rather than a style it would misread.
            if (count == 3 && parameters[start + 2] == (byte)'0')
            {
                Emit((byte)'2');
                Emit((byte)'4');
            }
            else
            {
                Emit((byte)'4');
            }

            return;
        }

        for (var index = 0; index < count; index++) Emit(parameters[start + index]);
    }

    private void Flush()
    {
        foreach (var value in _sequence) Emit(value);
        _sequence.Clear();
    }

    private void Emit(byte value)
    {
        if (_length == _output.Length) Array.Resize(ref _output, _output.Length * 2);
        _output[_length++] = value;
    }
}
