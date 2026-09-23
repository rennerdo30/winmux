using System.Text;
using Avalonia.Input;

namespace WinMux.Shell.Tests;

/// <summary>
/// What keys and pastes become for the program in a terminal pane. Written against what Claude Code
/// needs from Windows, since that is where the gaps were found: Alt+V to paste an image, Shift+Tab
/// to change mode, and multi-line pastes that do not run line by line.
/// </summary>
public sealed class TerminalInputTests
{
    private static string? Key(Key key, KeyModifiers modifiers = KeyModifiers.None, string? symbol = null, bool application = false) =>
        TerminalInput.EncodeKey(key, modifiers, symbol, application) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    [Fact]
    public void Alt_and_a_letter_is_escape_and_the_letter()
    {
        Assert.Equal("\u001bv", Key(Avalonia.Input.Key.V, KeyModifiers.Alt, "v"));
    }

    [Fact]
    public void Alt_and_a_letter_takes_its_case_from_shift_not_from_the_reported_symbol()
    {
        // What Windows really reports for Alt+V, from the user's key log: symbol "V", upper case,
        // with no Shift held. Sending that made it Alt+Shift+V.
        Assert.Equal("\u001bv", Key(Avalonia.Input.Key.V, KeyModifiers.Alt, "V"));
        Assert.Equal("\u001bV", Key(Avalonia.Input.Key.V, KeyModifiers.Alt | KeyModifiers.Shift, "V"));
    }

    [Fact]
    public void Alt_uses_the_character_the_layout_produces()
    {
        // On a Japanese keyboard Shift+2 is a double quote; the symbol, not the key, decides.
        Assert.Equal("\u001b\"", Key(Avalonia.Input.Key.D2, KeyModifiers.Alt | KeyModifiers.Shift, "\""));
    }

    [Fact]
    public void Alt_falls_back_to_the_key_when_the_platform_gives_no_symbol()
    {
        Assert.Equal("\u001bv", Key(Avalonia.Input.Key.V, KeyModifiers.Alt));
        Assert.Equal("\u001bV", Key(Avalonia.Input.Key.V, KeyModifiers.Alt | KeyModifiers.Shift));
    }

    [Fact]
    public void Ctrl_and_Alt_together_is_AltGr_and_left_to_text_input()
    {
        // German AltGr+Q is @. Sending ESC q would make the character impossible to type.
        Assert.Null(Key(Avalonia.Input.Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@"));
    }

    [Fact]
    public void Shift_tab_is_back_tab()
    {
        Assert.Equal("\u001b[Z", Key(Avalonia.Input.Key.Tab, KeyModifiers.Shift));
        Assert.Equal("\t", Key(Avalonia.Input.Key.Tab));
    }

    [Fact]
    public void Control_letters_are_control_characters()
    {
        Assert.Equal("\u0003", Key(Avalonia.Input.Key.C, KeyModifiers.Control));
        Assert.Equal("\u0016", Key(Avalonia.Input.Key.V, KeyModifiers.Control));
    }

    [Fact]
    public void Arrows_carry_their_modifiers_and_follow_application_mode()
    {
        Assert.Equal("\u001b[D", Key(Avalonia.Input.Key.Left));
        Assert.Equal("\u001bOD", Key(Avalonia.Input.Key.Left, application: true));
        Assert.Equal("\u001b[1;5D", Key(Avalonia.Input.Key.Left, KeyModifiers.Control));
        Assert.Equal("\u001b[1;2A", Key(Avalonia.Input.Key.Up, KeyModifiers.Shift));
    }

    [Fact]
    public void Function_and_editing_keys_are_xterms()
    {
        Assert.Equal("\u001bOP", Key(Avalonia.Input.Key.F1));
        Assert.Equal("\u001b[15~", Key(Avalonia.Input.Key.F5));
        Assert.Equal("\u001b[24;5~", Key(Avalonia.Input.Key.F12, KeyModifiers.Control));
        Assert.Equal("\u001b[3~", Key(Avalonia.Input.Key.Delete));
    }

    [Fact]
    public void Printable_keys_are_left_to_text_input()
    {
        Assert.Null(Key(Avalonia.Input.Key.V, KeyModifiers.None, "v"));
        Assert.Null(Key(Avalonia.Input.Key.V, KeyModifiers.Shift, "V"));
    }

    [Fact]
    public void A_bracketed_paste_is_wrapped_and_cannot_close_itself_early()
    {
        var pasted = Encoding.UTF8.GetString(TerminalInput.EncodePaste("line one\r\nline two\u001b[201~rm -rf /\n", bracketed: true));

        Assert.StartsWith("\u001b[200~", pasted);
        Assert.EndsWith("\u001b[201~", pasted);
        Assert.Equal(1, pasted.Split("\u001b[201~").Length - 1);
        Assert.Contains("line one\rline two", pasted);
    }

    [Fact]
    public void An_unbracketed_paste_sends_carriage_returns_for_line_breaks()
    {
        Assert.Equal("a\rb\rc", Encoding.UTF8.GetString(TerminalInput.EncodePaste("a\r\nb\nc", bracketed: false)));
    }
}
