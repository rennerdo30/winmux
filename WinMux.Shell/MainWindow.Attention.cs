using Avalonia.Controls;
using Avalonia.Threading;
using WinMux.Core.Model;
using WinMux.Panes;
using WinMux.Shell.Notifications;
using WinMux.Terminal;

namespace WinMux.Shell;

/// <summary>
/// Programs in panes asking for attention, and getting the user back to them.
///
/// A terminal program — Claude Code, most visibly — says it needs the user with an OSC 9/777/99
/// message or a bell. When that pane is not the one being looked at, it becomes a Windows
/// notification naming the pane; clicking it brings WinMux forward with that pane focused, tab and
/// all. The status bar says it too, for anyone who has notifications turned off.
/// </summary>
internal sealed partial class MainWindow
{
    private readonly TerminalAttention _attention = new();

    private void WireAttention(Pane pane, IPaneRuntime runtime)
    {
        if (runtime is not IAttentionRuntime source) return;
        var id = pane.Id;
        source.AttentionRequested += notification => OnAttentionRequested(id, notification);
    }

    private void OnAttentionRequested(PaneId id, TerminalNotification notification)
    {
        if (_tree.Find(id)?.Pane is not { } pane) return;

        var looking = IsActive && _tree.Focused == id;
        var policy = Settings.ShellSettings.Current.TerminalNotifications;
        if (!_attention.ShouldNotify(policy, notification, id, looking, DateTimeOffset.UtcNow)) return;

        var (title, message) = TerminalAttention.Compose(pane.Title, notification);
        _message = $"{title}: {message}";
        UpdateStatus();

        var notifier = PlatformServices.Notifications;
        if (!notifier.IsAvailable) return;

        if (notifier.BlockedReason is { } blocked)
        {
            // Shown anyway — the user may switch them on and look in the notification centre — but
            // said here, because otherwise WinMux looks as if it ignored the request.
            _message = $"{title}: {message} · {blocked}";
            UpdateStatus();
        }

        // The click arrives on the notifier's own thread.
        notifier.Show(title, message, () => Dispatcher.UIThread.Post(() => ReturnTo(id)));
    }

    /// <summary>Bring WinMux forward with the pane that asked in focus — revealing its tab if it is behind one.</summary>
    private void ReturnTo(PaneId id)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_tree.Find(id) is not null) FocusPane(id);
    }
}
