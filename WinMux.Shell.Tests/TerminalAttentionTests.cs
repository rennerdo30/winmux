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
