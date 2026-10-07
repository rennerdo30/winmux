using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

public sealed class VerticalTabStyleTests
{
    [Theory]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public Task Side_tabs_are_compact_aligned_rows_with_grouped_actions(TabStripPlacement placement) =>
        Headless.RunSync(() =>
        {
            var pinned = Pane.Terminal("sprint tools");
            pinned.IsPinned = true;
            var empty = Pane.Terminal("empty pane");
            var stack = new StackNode([new LeafNode(pinned), new LeafNode(empty)], activeIndex: 1);
            var commands = new TabStripCommands(_ => { }, _ => { }, _ => { }, _ => { },
                (_, _, _) => { }, _ => { }, (_, _) => { }, _ => { });
            var strip = TabStripView.Build(new WinMux.Core.Layout.TabStrip(stack,
                new WinMux.Core.Layout.Rect(0, 0, 220, 320), placement), empty.Id, commands);
            var window = new Window { Width = 220, Height = 320, Content = strip };
            window.Styles.Add(Theme.Build());
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var tabs = strip.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains(Theme.Tab)).ToArray();
                Assert.Equal(2, tabs.Length);
                Assert.All(tabs, tab => Assert.Equal(Palette.ControlHeight, tab.Bounds.Height));
                var labels = tabs.Select(tab => tab.GetVisualDescendants().OfType<TextBlock>().First()).ToArray();
                Assert.Equal(labels[0].TranslatePoint(default, strip)!.Value.X,
                    labels[1].TranslatePoint(default, strip)!.Value.X);
                var corners = tabs.Select(tab => tab.GetVisualDescendants().OfType<Button>().Single()).ToArray();
                Assert.All(corners, corner => Assert.Equal(24, corner.Bounds.Height));
                Assert.Equal(corners[0].TranslatePoint(default, strip)!.Value.X,
                    corners[1].TranslatePoint(default, strip)!.Value.X);
                var actions = strip.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains(Theme.IconButton)).ToArray();
                Assert.Equal(2, actions.Length);
                Assert.Equal(actions[0].TranslatePoint(default, strip)!.Value.Y,
                    actions[1].TranslatePoint(default, strip)!.Value.Y);
                Assert.IsType<StackPanel>(actions[0].Parent);
                Assert.Equal(Orientation.Horizontal, ((StackPanel)actions[0].Parent!).Orientation);
            }
            finally { window.Close(); }
        });
}
