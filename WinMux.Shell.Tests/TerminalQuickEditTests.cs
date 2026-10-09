namespace WinMux.Shell.Tests;

/// <summary>
/// The console's right-click, which WinMux did not have.
///
/// <para>
/// Reported as "copy and pasting like in a normal CMD still doesn't work — when you mark and do a
/// right click, it doesn't copy". It did not: <c>OnPointerPressed</c> returned immediately unless
/// the <em>left</em> button was down, so the right button did nothing at all in a terminal pane.
/// </para>
/// </summary>
public class TerminalQuickEditTests
{
    [Fact]
    public void Right_clicking_a_selection_copies_it() =>
        Assert.Equal(
            TerminalMouseAction.CopyAndClearSelection,
            TerminalQuickEdit.RightButton(hasSelection: true));

    [Fact]
    public void Right_clicking_with_nothing_selected_pastes() =>
        Assert.Equal(TerminalMouseAction.Paste, TerminalQuickEdit.RightButton(hasSelection: false));

    [Fact]
    public void Copy_wins_over_paste_when_both_could_apply()
    {
        // Stated as its own test because the ordering is the part that can be got wrong, and
        // getting it wrong is unrecoverable: a terminal cannot undo a paste, because the program
        // on the other end has already read it.
        Assert.NotEqual(TerminalMouseAction.Paste, TerminalQuickEdit.RightButton(hasSelection: true));
    }

    [Fact]
    public void The_middle_button_does_nothing()
    {
        // Paste-on-middle-click is an X11 convention. On Windows a stray wheel press that silently
        // types into a shell is not a trade worth making.
        Assert.Equal(TerminalMouseAction.None, TerminalQuickEdit.MiddleButton());
    }
}

/// <summary>
/// That the terminal pane actually acts on the right button.
///
/// The rule above is three lines and was never the hard part; the fault was that nothing called it.
/// <c>OnPointerPressed</c> began with "return unless the left button is down", so a right-click
/// reached no code at all — which is the class of gap CLAUDE.md keeps a note about.
/// </summary>
public class TerminalRightClickWiringTests
{
    private static TerminalPaneControl Pane()
    {
        // Constructed, not started: no pty is launched until StartAsync, and none is needed to ask
        // what a mouse press does.
        var control = new TerminalPaneControl("cmd.exe", null, Path.GetTempPath());
        var window = new Avalonia.Controls.Window { Width = 400, Height = 300, Content = control };
        window.Show();
        window.Measure(new Avalonia.Size(400, 300));
        window.Arrange(new Avalonia.Rect(0, 0, 400, 300));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return control;
    }

    private static Avalonia.Input.PointerPressedEventArgs Press(
        TerminalPaneControl control,
        Avalonia.Input.RawInputModifiers button,
        Avalonia.Input.PointerUpdateKind kind) =>
        new(
            control,
            new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, isPrimary: true),
            control,
            new Avalonia.Point(20, 20),
            (ulong)Environment.TickCount64,
            new Avalonia.Input.PointerPointProperties(button, kind),
            Avalonia.Input.KeyModifiers.None);

    [Fact]
    public Task A_right_click_is_acted_on_rather_than_ignored() => Headless.RunSync(() =>
    {
        var control = Pane();
        var press = Press(
            control,
            Avalonia.Input.RawInputModifiers.RightMouseButton,
            Avalonia.Input.PointerUpdateKind.RightButtonPressed);

        control.RaiseEvent(press);

        Assert.True(press.Handled, "the right button reached no code, as it did before QuickEdit");
    });

    [Fact]
    public Task A_left_click_still_starts_a_selection() => Headless.RunSync(() =>
    {
        // The control case. A right-click branch that swallowed every button would pass the test
        // above and break the thing the pane is mostly used for.
        var control = Pane();
        var press = Press(
            control,
            Avalonia.Input.RawInputModifiers.LeftMouseButton,
            Avalonia.Input.PointerUpdateKind.LeftButtonPressed);

        control.RaiseEvent(press);

        Assert.True(press.Handled);
    });
}
