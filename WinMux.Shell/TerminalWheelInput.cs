using System.Text;
using Avalonia.Input;
using WinMux.Terminal;

namespace WinMux.Shell;

internal readonly record struct TerminalWheelAction(byte[] Bytes, int HistoryRows);

/// <summary>xterm wheel reporting; Shift bypasses the app to inspect the terminal's history.</summary>
internal sealed class TerminalWheelInput
{
    private double _remainder;
    private (bool Reporting, bool Sgr, bool Alternate, bool AlternateScroll, bool Shift)? _lastRoute;

    public TerminalWheelAction Route(double delta, int column, int row, KeyModifiers modifiers,
        TerminalMouseMode mode)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var route = (mode.Reporting, mode.Sgr, mode.AlternateScreen, mode.AlternateScroll, shift);
        if (_lastRoute != route) _remainder = 0;
        _lastRoute = route;
        if (!double.IsFinite(delta)) return new([], 0);
        var toProgram = !shift && (mode.Reporting || (mode.AlternateScreen && mode.AlternateScroll));
        // Mouse-aware apps choose their own scroll speed. History and alternate-scroll use 3 lines.
        var scale = toProgram && mode.Reporting ? 1 : 3;
        _remainder += Math.Clamp(delta * scale, -100, 100);
        var steps = (int)_remainder;
        _remainder -= steps;
        if (steps == 0) return new([], 0);
        if (!toProgram) return new([], steps);
        column = Math.Max(0, column);
        row = Math.Max(0, row);
        // X10 encodes coordinates as bytes with a 33 offset. Never wrap a large pane's coordinates.
        if (mode.Reporting && !mode.Sgr && (column > 222 || row > 222)) return new([], 0);
        var bytes = new List<byte>();
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            if (mode.Reporting)
            {
                var button = (steps > 0 ? 64 : 65) +
                    (modifiers.HasFlag(KeyModifiers.Alt) ? 8 : 0) +
                    (modifiers.HasFlag(KeyModifiers.Control) ? 16 : 0);
                if (mode.Sgr) bytes.AddRange(Encoding.ASCII.GetBytes($"\u001b[<{button};{column + 1};{row + 1}M"));
                else bytes.AddRange([0x1b, (byte)'[', (byte)'M', (byte)(button + 32), (byte)(column + 33), (byte)(row + 33)]);
            }
            else bytes.AddRange(TerminalInput.EncodeKey(steps > 0 ? Key.Up : Key.Down,
                modifiers, null, mode.ApplicationCursorKeys)!);
        }
        return new(bytes.ToArray(), 0);
    }
}
