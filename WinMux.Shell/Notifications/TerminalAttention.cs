using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Terminal;

namespace WinMux.Shell.Notifications;

/// <summary>A pane runtime whose program can ask for the user's attention.</summary>
internal interface IAttentionRuntime
{
    /// <summary>Raised on the UI thread.</summary>
    event Action<TerminalNotification>? AttentionRequested;
}

/// <summary>
/// Whether a request for attention from a pane becomes a desktop notification, and what it says.
///
/// Kept apart from the window so the rules can be tested, and the rules are the product here: a
/// notification about the pane already in front of you is noise, a program that rings the bell in
/// a loop must not bury the notification centre, and the setting decides whether a bare bell counts.
/// </summary>
internal sealed class TerminalAttention
{
    /// <summary>At most one notification per pane in this span, however often it asks.</summary>
    public static readonly TimeSpan PerPaneQuiet = TimeSpan.FromSeconds(3);

    /// <summary>The same words from the same pane are not repeated inside this span.</summary>
    public static readonly TimeSpan RepeatQuiet = TimeSpan.FromSeconds(30);

    private readonly Dictionary<PaneId, (DateTimeOffset At, string Text)> _last = [];

    /// <param name="looking">
    /// The user is looking at this pane: WinMux is the active window and this is the focused pane.
    /// </param>
    public bool ShouldNotify(
        TerminalNotificationPolicy policy,
        TerminalNotification notification,
        PaneId pane,
        bool looking,
        DateTimeOffset now)
    {
        if (policy == TerminalNotificationPolicy.Off) return false;
        if (notification.Kind == TerminalNotificationKind.Bell && policy != TerminalNotificationPolicy.MessagesAndBells)
            return false;
        if (looking) return false;

        var text = $"{notification.Title}\n{notification.Body}";
        if (_last.TryGetValue(pane, out var last))
        {
            if (now - last.At < PerPaneQuiet) return false;
            if (last.Text == text && now - last.At < RepeatQuiet) return false;
        }

        _last[pane] = (now, text);
        return true;
    }

    /// <summary>Forget a pane that has closed.</summary>
    public void Forget(PaneId pane) => _last.Remove(pane);

    /// <summary>
    /// The heading names the pane, because "Claude needs your permission" is only useful once you
    /// know which of six Claude sessions said it.
    /// </summary>
    public static (string Title, string Message) Compose(string paneTitle, TerminalNotification notification)
    {
        var pane = string.IsNullOrWhiteSpace(paneTitle) ? "A terminal pane" : paneTitle.Trim();
        var title = notification.Title is { Length: > 0 } heading ? $"{pane} · {heading}" : pane;
        var message = notification.Kind == TerminalNotificationKind.Bell || notification.Body.Length == 0
            ? "Rang the bell. It may be waiting for you."
            : notification.Body;
        return (title, message);
    }
}
