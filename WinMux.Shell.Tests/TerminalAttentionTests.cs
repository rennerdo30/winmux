using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Shell.Notifications;
using WinMux.Terminal;

namespace WinMux.Shell.Tests;

/// <summary>
/// When a program's request for attention becomes a Windows notification. The rules exist to keep it
/// from being noise: never for the pane in front of you, never a flood, and a bare bell only when the
/// user has said bells count.
/// </summary>
public sealed class TerminalAttentionTests
{
    private static readonly TerminalNotification Message = new(TerminalNotificationKind.Osc9, null, "Claude needs your permission");
    private static readonly TerminalNotification Bell = new(TerminalNotificationKind.Bell, null, "");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_for_the_pane_being_looked_at()
    {
        var attention = new TerminalAttention();

        Assert.False(attention.ShouldNotify(TerminalNotificationPolicy.MessagesAndBells, Message, PaneId.New(), looking: true, Now));
    }

    [Fact]
    public void A_message_from_a_pane_out_of_sight_is_shown()
    {
        var attention = new TerminalAttention();

        Assert.True(attention.ShouldNotify(TerminalNotificationPolicy.Messages, Message, PaneId.New(), looking: false, Now));
    }

    [Theory]
    [InlineData(TerminalNotificationPolicy.MessagesAndBells, true)]
    [InlineData(TerminalNotificationPolicy.Messages, false)]
    [InlineData(TerminalNotificationPolicy.Off, false)]
    public void A_bell_counts_only_when_bells_are_asked_for(TerminalNotificationPolicy policy, bool expected)
    {
        Assert.Equal(expected, new TerminalAttention().ShouldNotify(policy, Bell, PaneId.New(), looking: false, Now));
    }

    [Fact]
    public void Off_means_off()
    {
        Assert.False(new TerminalAttention().ShouldNotify(TerminalNotificationPolicy.Off, Message, PaneId.New(), looking: false, Now));
    }

    [Fact]
    public void A_pane_that_asks_in_a_loop_is_heard_once_per_quiet_period()
    {
        var attention = new TerminalAttention();
        var pane = PaneId.New();
        const TerminalNotificationPolicy policy = TerminalNotificationPolicy.MessagesAndBells;

        Assert.True(attention.ShouldNotify(policy, Bell, pane, false, Now));
        Assert.False(attention.ShouldNotify(policy, Bell, pane, false, Now.AddSeconds(1)));
        Assert.True(attention.ShouldNotify(policy, Message, pane, false, Now.AddSeconds(4)));

        // Another pane is not held back by this one.
        Assert.True(attention.ShouldNotify(policy, Bell, PaneId.New(), false, Now.AddSeconds(4)));
    }

    [Fact]
    public void The_same_words_are_not_repeated_for_half_a_minute()
    {
        var attention = new TerminalAttention();
        var pane = PaneId.New();
        const TerminalNotificationPolicy policy = TerminalNotificationPolicy.Messages;

        Assert.True(attention.ShouldNotify(policy, Message, pane, false, Now));
        Assert.False(attention.ShouldNotify(policy, Message, pane, false, Now.AddSeconds(10)));
        Assert.True(attention.ShouldNotify(policy, Message, pane, false, Now.AddSeconds(31)));
    }

    [Fact]
    public void The_heading_names_the_pane()
    {
        var (title, message) = TerminalAttention.Compose(
            "api server",
            new TerminalNotification(TerminalNotificationKind.Osc777, "Build", "done"));

        Assert.Equal("api server · Build", title);
        Assert.Equal("done", message);
    }

    [Fact]
    public void A_bell_is_put_into_words()
    {
        var (title, message) = TerminalAttention.Compose("build", Bell);

        Assert.Equal("build", title);
        Assert.Contains("bell", message, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The mark a waiting pane leaves on its tab.
///
/// It answers a different question from the notification — not "tell me now" but "which of these
/// six panes wants me" — so it follows different rules: no quiet periods, and it lasts until the
/// pane is looked at rather than until a toast has been shown.
/// </summary>
public sealed class WaitingPaneMarkTests
{
    private static readonly TerminalNotification Message =
        new(TerminalNotificationKind.Osc9, null, "Claude needs your permission");

    private static readonly TerminalNotification Bell = new(TerminalNotificationKind.Bell, null, "");

    [Fact]
    public void A_marked_pane_is_waiting_and_remembers_what_it_said()
    {
        var attention = new TerminalAttention();
        var pane = PaneId.New();

        attention.Mark(pane, "Claude needs your permission");

        Assert.True(attention.IsWaiting(pane));
        Assert.Equal("Claude needs your permission", attention.WaitingMessage(pane));
    }

    [Fact]
    public void An_unmarked_pane_is_not_waiting()
    {
        var attention = new TerminalAttention();

        Assert.False(attention.IsWaiting(PaneId.New()));
        Assert.Null(attention.WaitingMessage(PaneId.New()));
    }

    [Fact]
    public void Looking_at_a_pane_clears_it()
    {
        var attention = new TerminalAttention();
        var pane = PaneId.New();
        attention.Mark(pane, "anything");

        Assert.True(attention.Seen(pane));
        Assert.False(attention.IsWaiting(pane));

        // And says there was nothing to clear the second time, so the caller can skip a relayout.
        Assert.False(attention.Seen(pane));
    }

    [Fact]
    public void One_pane_waiting_does_not_mark_another()
    {
        var attention = new TerminalAttention();
        var asking = PaneId.New();
        var quiet = PaneId.New();
        attention.Mark(asking, "over here");

        Assert.True(attention.IsWaiting(asking));
        Assert.False(attention.IsWaiting(quiet));
    }

    [Fact]
    public void A_closed_pane_is_forgotten()
    {
        var attention = new TerminalAttention();
        var pane = PaneId.New();
        attention.Mark(pane, "anything");

        attention.Forget(pane);

        Assert.False(attention.IsWaiting(pane));
    }

    [Theory]
    [InlineData(TerminalNotificationPolicy.Off, false)]
    [InlineData(TerminalNotificationPolicy.Messages, true)]
    [InlineData(TerminalNotificationPolicy.MessagesAndBells, true)]
    public void A_message_is_marked_unless_notifications_are_off(TerminalNotificationPolicy policy, bool expected) =>
        Assert.Equal(expected, TerminalAttention.ShouldMark(policy, Message));

    [Theory]
    [InlineData(TerminalNotificationPolicy.Off, false)]
    [InlineData(TerminalNotificationPolicy.Messages, false)]
    [InlineData(TerminalNotificationPolicy.MessagesAndBells, true)]
    public void A_bare_bell_is_marked_only_when_bells_count(TerminalNotificationPolicy policy, bool expected) =>
        Assert.Equal(expected, TerminalAttention.ShouldMark(policy, Bell));

    [Fact]
    public void A_pane_ringing_in_a_loop_stays_marked_without_being_rate_limited()
    {
        // ShouldNotify has quiet periods because a flood of toasts buries the notification centre.
        // A mark that is already showing cannot be shown twice, so there is nothing to limit, and a
        // program still ringing is a program that still wants you.
        var attention = new TerminalAttention();
        var pane = PaneId.New();
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        Assert.True(attention.ShouldNotify(TerminalNotificationPolicy.Messages, Message, pane, looking: false, now));
        Assert.False(attention.ShouldNotify(TerminalNotificationPolicy.Messages, Message, pane, looking: false, now));

        Assert.True(TerminalAttention.ShouldMark(TerminalNotificationPolicy.Messages, Message));
    }
}
