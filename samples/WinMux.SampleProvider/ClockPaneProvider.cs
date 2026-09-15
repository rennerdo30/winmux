using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.SampleProvider;

/// <summary>
/// A pane kind that did not ship with WinMux, to prove that one can exist.
///
/// ADR 0012 made `PaneKind` a string so a provider in another assembly could define its own kind.
/// That claim went unverified for two phases, because nothing loaded an external provider — the
/// extension point was real in the type system and nowhere else. This is the smallest thing that
/// tests it end to end: drop the build output into <c>providers/WinMux.SampleProvider/</c> beside
/// <c>WinMux.exe</c> and a <c>com.example.clock</c> pane becomes something WinMux can open and
/// restore.
///
/// It is also the reference for what a provider has to supply: a kind, a way to create a runtime,
/// and a way to restore one from a descriptor it wrote itself.
/// </summary>
public sealed class ClockPaneProvider : IPaneProvider
{
    public PaneKind Kind { get; } = PaneKind.Create("com.example.clock");

    public ValueTask<IPaneRuntime> CreateAsync(
        PaneProviderContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IPaneRuntime>(new ClockPaneRuntime(context, Kind));

    /// <summary>
    /// Restoring is creating, for a pane whose whole state is the current time.
    ///
    /// A provider with real state reads it back out of <c>context.Descriptor.Extras</c>, which
    /// round-trips through the session file untouched even when this provider is not installed.
    /// </summary>
    public ValueTask<IPaneRuntime> RestoreAsync(
        PaneProviderContext context,
        CancellationToken cancellationToken = default) =>
        CreateAsync(context, cancellationToken);
}

internal sealed class ClockPaneRuntime : IPaneRuntime
{
    private readonly TextBlock _time;
    private readonly DispatcherTimer _timer;
    private readonly RestoreDescriptor _descriptor;

    public ClockPaneRuntime(PaneProviderContext context, PaneKind kind)
    {
        ArgumentNullException.ThrowIfNull(context);

        PaneId = context.PaneId;
        Kind = kind;
        _descriptor = context.Descriptor with { Kind = kind, Title = "clock" };

        _time = new TextBlock
        {
            FontSize = 48,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        View = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)),
            CornerRadius = new Avalonia.CornerRadius(8),
            Child = _time,
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Show();
        _timer.Start();
        Show();
    }

    public PaneId PaneId { get; }
    public PaneKind Kind { get; }
    public Control View { get; }
    public string? StatusMessage => null;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public bool Focus() => View.Focus();

    public void Arrange(PaneArrangement arrangement) { }

    public void RefreshRestoreState() { }

    public RestoreDescriptor CaptureRestoreDescriptor() => _descriptor;

    public ValueTask<PaneCloseResult> CloseAsync(
        PaneCloseReason reason,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PaneCloseResult.Success());

    public ValueTask DisposeAsync()
    {
        _timer.Stop();
        return ValueTask.CompletedTask;
    }

    private void Show() => _time.Text = DateTime.Now.ToString("HH:mm:ss");
}
