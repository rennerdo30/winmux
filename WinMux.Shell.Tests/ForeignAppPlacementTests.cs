using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using WinMux.Core.Model;
using WinMux.Panes;
using WinMux.Platform;
using WinMux.Shell.Panes;
using LayoutRect = WinMux.Core.Layout.Rect;

namespace WinMux.Shell.Tests;

public sealed class ForeignAppPlacementTests
{
    [Fact]
    public async Task Right_hand_app_uses_arrangement_origin_before_its_view_is_laid_out()
    {
        await Headless.RunSync(() =>
        {
            var host = WindowHandle.FromPlatformValue(1234);
            var windows = new RecordingWindows(host);
            using var tracker = new ForeignWindowTracker(windows, runThread: false);
            var pane = new Pane(PaneId.New(), PaneKind.ForeignApp, "test app", new RestoreDescriptor
            {
                Kind = PaneKind.ForeignApp,
                Title = "test app",
            });
            var runtime = new ForeignAppPaneRuntime(pane, tracker, [], WindowHandle.None, windows);
            var app = (ForeignAppPane)typeof(ForeignAppPaneRuntime)
                .GetField("_app", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
            typeof(ForeignAppPane).GetProperty(nameof(ForeignAppPane.Hwnd))!.SetValue(app, host);

            var canvas = new Canvas();
            var window = new Window { Width = 800, Height = 500, Content = canvas };
            window.Show();
            try
            {
                canvas.Children.Add(runtime.View);
                Canvas.SetLeft(runtime.View, 420);
                var rect = new LayoutRect(420, 25, 340, 400);
                var expected = canvas.PointToScreen(new Point(rect.X, rect.Y));

                // The View has not had a layout pass since it was moved to the right column.
                runtime.Arrange(new PaneArrangement(rect, IsVisible: true));
                tracker.ApplyPending();

                var placed = Assert.Single(windows.Placements);
                Assert.Equal(expected.X, placed.Bounds.X);
                Assert.Equal(expected.Y, placed.Bounds.Y);
            }
            finally { window.Close(); }
        });
    }

    private sealed class RecordingWindows(WindowHandle alive) : IHostWindowService
    {
        public List<WindowPlacement> Placements { get; } = [];
        public bool IsAlive(WindowHandle window) => window == alive;
        public void Apply(WindowHandle window, WindowPlacement placement) => Placements.Add(placement);
    }
}
