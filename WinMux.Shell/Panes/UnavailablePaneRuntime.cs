using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.Shell.Panes;

/// <summary>Keeps an unrestorable descriptor visible and persistable instead of dropping it.</summary>
internal sealed class UnavailablePaneRuntime : IPaneRuntime
{
    private readonly RestoreDescriptor _descriptor;

    public UnavailablePaneRuntime(Pane pane, string message)
    {
        PaneId = pane.Id;
        Kind = pane.Kind;
        _descriptor = pane.Restore;
        StatusMessage = message;
        View = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x45, 0x27, 0x35)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xbf, 0x61, 0x6a)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Margin = new Thickness(14),
                TextWrapping = TextWrapping.Wrap,
                Text = $"{pane.Title}\n\n{message}\n\nThe restore descriptor was preserved.",
            },
        };
    }

    public PaneId PaneId { get; }
    public PaneKind Kind { get; }
    public Control View { get; }
    public string? StatusMessage { get; }
    public event EventHandler? StateChanged { add { } remove { } }
    public bool Focus() => View.Focus();
    public void Arrange(PaneArrangement arrangement) { }
    public void RefreshRestoreState() { }
    public RestoreDescriptor CaptureRestoreDescriptor() => _descriptor;
    public ValueTask<PaneCloseResult> CloseAsync(PaneCloseReason reason, CancellationToken token = default) =>
        ValueTask.FromResult(PaneCloseResult.Success());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
