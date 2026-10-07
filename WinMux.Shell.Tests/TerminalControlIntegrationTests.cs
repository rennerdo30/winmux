using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using WinMux.Pty;
using WinMux.Terminal;

namespace WinMux.Shell.Tests;

/// <summary>Exercise real control event routing and VT parsing against a recording PTY, without processes.</summary>
public sealed class TerminalControlIntegrationTests
{
    [Fact]
    public Task Wheel_events_reach_a_mouse_reporting_application_as_SGR_input() => Headless.RunSync(() =>
    {
        using var terminal = new Fixture();
        terminal.Output("\u001b[?1000h\u001b[?1006h");
        Dispatcher.UIThread.RunJobs();
        terminal.Pty.Writes.Clear();

        terminal.Window.MouseWheel(new Avalonia.Point(8, 8), new Avalonia.Vector(0, 1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var up = Assert.Single(terminal.Pty.Writes);
        Assert.Matches("^\\x1b\\[<64;[1-9][0-9]*;[1-9][0-9]*M$", up);
        Assert.Equal(0, terminal.Viewport.ScrollOffset);
        terminal.Pty.Writes.Clear();
        terminal.Window.MouseWheel(new Avalonia.Point(8, 8), new Avalonia.Vector(0, -1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Matches("^\\x1b\\[<65;[1-9][0-9]*;[1-9][0-9]*M$", Assert.Single(terminal.Pty.Writes));
    });

    [Fact]
    public Task Cursor_query_response_preserves_the_scrolled_view_and_selection() => Headless.RunSync(() =>
    {
        using var terminal = new Fixture();
        terminal.Output(string.Concat(Enumerable.Range(0, 150).Select(i => $"line {i}\r\n")));
        Dispatcher.UIThread.RunJobs();
        Assert.True(terminal.Engine.ScrollbackCount > 10);
        Assert.True(terminal.Viewport.Scroll(10, terminal.Engine.ScrollbackCount));
        terminal.Viewport.BeginSelection(new TerminalPosition(0, 0));
        terminal.Viewport.ExtendSelection(new TerminalPosition(1, 3));
        var selection = terminal.Viewport.Selection;
        terminal.Pty.Writes.Clear();

        terminal.Output("\u001b[6n");
        Dispatcher.UIThread.RunJobs();

        Assert.Matches("^\\x1b\\[[1-9][0-9]*;[1-9][0-9]*R$", Assert.Single(terminal.Pty.Writes));
        Assert.Equal(10, terminal.Viewport.ScrollOffset);
        Assert.Equal(selection, terminal.Viewport.Selection);
        Assert.True(terminal.Viewport.HasSelection);
    });

    [Fact]
    public Task Title_and_output_floods_publish_the_latest_title_after_keyboard_input() => Headless.RunSync(() =>
    {
        using var terminal = new Fixture();
        terminal.Control.Focus();
        Dispatcher.UIThread.RunJobs();
        var order = new List<string>();
        var titles = new List<string>();
        terminal.Control.TitleChanged += title => { titles.Add(title); order.Add("title"); };
        terminal.Pty.OnWrite = _ => order.Add("input");
        terminal.Pty.Writes.Clear();
        for (var i = 0; i < 1000; i++) terminal.Output($"\u001b]0;title {i}\u0007x");
        Assert.Empty(titles);
        // Headless KeyPress flushes queued jobs before injecting input; raise the routed event
        // directly here so the test retains the competing dispatcher priorities.
        Dispatcher.UIThread.Post(() => terminal.Control.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.B, KeyModifiers = KeyModifiers.Control,
            PhysicalKey = PhysicalKey.B, KeySymbol = "b",
        }),
            DispatcherPriority.Input);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["title 999"], titles);
        Assert.Equal("\u0002", Assert.Single(terminal.Pty.Writes));
        Assert.Equal(["input", "title"], order);
    });

    private sealed class Fixture : IDisposable
    {
        public TerminalPaneControl Control { get; } = new("cmd.exe", [], Environment.CurrentDirectory);
        public RecordingPty Pty { get; } = new();
        public ITerminalEngine Engine { get; }
        public TerminalViewport Viewport { get; }
        public Window Window { get; }

        public Fixture()
        {
            // Production construction stays unchanged: only its PTY boundary is replaced.
            Field("_session").SetValue(Control, Pty);
            Engine = (ITerminalEngine)Field("_engine").GetValue(Control)!;
            Viewport = (TerminalViewport)Field("_viewport").GetValue(Control)!;
            Window = new Window { Width = 480, Height = 240, Content = Control };
            Window.Show();
            Window.Measure(new Avalonia.Size(480, 240));
            Window.Arrange(new Avalonia.Rect(0, 0, 480, 240));
            Dispatcher.UIThread.RunJobs();
        }

        private static FieldInfo Field(string name) => typeof(TerminalPaneControl)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

        public void Output(string text) => Engine.Write(Encoding.UTF8.GetBytes(text));
        public void Dispose() { Control.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private sealed class RecordingPty : IPtySession
    {
        public int ProcessId => 12345;
        public Stream Output => Stream.Null;
        public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;
        public List<string> Writes { get; } = [];
        public Action<string>? OnWrite { get; set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(data.Span);
            Writes.Add(text);
            OnWrite?.Invoke(text);
            return ValueTask.CompletedTask;
        }
        public void Resize(int columns, int rows) { }
        public void Dispose() { }
    }
}
