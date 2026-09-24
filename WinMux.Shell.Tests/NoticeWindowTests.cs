using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WinMux.Shell.Tests;

/// <summary>
/// What a notice does with a message long enough to wrap.
///
/// <para>
/// <b>These do not guard the fault that prompted them.</b> A message was reported cut off at the
/// bottom edge on Windows, and Avalonia's headless windows size this dialog correctly with the fix
/// and without it — so a test here passes either way. Saying that plainly is worth more than a
/// green tick that means nothing: the guard against clipping is the re-measure in
/// <c>NoticeWindow</c>'s <c>Opened</c> handler, and it is checked by looking at the real window.
/// </para>
///
/// <para>
/// What these do pin is the width the text is laid out at, which is what makes the height
/// predictable in the first place, and that a notice stays a small dialog for a short message.
/// </para>
/// </summary>
public sealed class NoticeWindowTests
{
    private const string Long =
        "No saved connections were found. WinMux looks for PuTTY, WinSCP, FileZilla, MobaXterm " +
        "and mRemoteNG where each of them normally keeps its sessions.";

    private static (Window Window, TextBlock Body) Show(string message)
    {
        var window = new NoticeWindow("Saved connections", message);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(520, 2000));
        window.Arrange(new Rect(0, 0, 520, window.DesiredSize.Height));
        Dispatcher.UIThread.RunJobs();

        var body = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == message);
        return (window, body);
    }

    [Fact]
    public Task It_really_did_wrap_to_several_lines() => Headless.RunSync(() =>
    {
        // The control case: a test that passes because the text fit on one line would prove
        // nothing at all about the fault it is guarding.
        var (_, body) = Show(Long);

        Assert.True(body.Bounds.Height > 40,
            $"the message occupied {body.Bounds.Height:0}px, so it did not wrap and this test checks nothing");
        Assert.True(body.Bounds.Width <= 480, $"the message was laid out {body.Bounds.Width:0}px wide");
    });

    [Fact]
    public Task A_very_long_message_makes_a_taller_window() => Headless.RunSync(() =>
    {
        var (window, _) = Show(string.Join(" ", Enumerable.Repeat(Long, 4)));

        Assert.True(window.Bounds.Height > 300, "the window did not grow to hold the text");
    });

    [Fact]
    public Task A_one_line_message_is_still_a_small_dialog() => Headless.RunSync(() =>
    {
        var (window, _) = Show("Done.");

        Assert.True(window.Bounds.Height < 200, $"a one-line notice was {window.Bounds.Height:0}px tall");
    });
}
