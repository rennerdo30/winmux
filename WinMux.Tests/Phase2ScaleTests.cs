using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

public sealed class Phase2ScaleTests
{
    [Fact]
    public void Eight_windows_with_sixty_four_panes_each_round_trip_without_loss()
    {
        var windows = Enumerable.Range(0, 8).Select(CreateWindow).ToArray();
        var snapshot = new SessionSnapshot
        {
            SavedAt = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
            Windows = windows,
        };

        var toml = SessionFile.Serialize(snapshot);
        var restored = SessionFile.Deserialize(toml);

        Assert.Equal(8, restored.Windows.Count);
        Assert.Equal(512, restored.Windows.Sum(window => SessionMapper.FromSnapshot(window).Panes.Count()));
        for (var index = 0; index < restored.Windows.Count; index++)
        {
            var window = restored.Windows[index];
            var tree = SessionMapper.FromSnapshot(window);
            Assert.Equal($"scale-{index}", window.Title);
            Assert.Equal(new Rect(index * 40, index * 30, 1200, 800), window.Bounds);
            Assert.Equal(window.FocusedPane, tree.Focused.Value);
            Assert.All(tree.Panes, pane =>
            {
                Assert.True(pane.Restore.Cwd.IsKnown);
                Assert.Equal(CwdSource.ShellReported, pane.Restore.Cwd.Source);
                Assert.Single(pane.Restore.Args);
                Assert.Single(pane.Restore.EnvOverrides);
            });
        }
    }

    private static WindowSnapshot CreateWindow(int windowIndex)
    {
        var first = CreatePane(windowIndex, 0);
        var tree = new LayoutTree(first);
        for (var paneIndex = 1; paneIndex < 64; paneIndex++)
        {
            var direction = paneIndex % 2 == 0 ? SplitDirection.Columns : SplitDirection.Rows;
            tree.Split(tree.Focused, direction, CreatePane(windowIndex, paneIndex), ratio: 0.5);
        }

        tree.Bounds = new Rect(windowIndex * 40, windowIndex * 30, 1200, 800);
        return SessionMapper.ToSnapshot(tree, $"scale-{windowIndex}");
    }

    private static Pane CreatePane(int windowIndex, int paneIndex) => new(
        PaneId.New(),
        PaneKind.Terminal,
        $"pane-{windowIndex}-{paneIndex}",
        new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Title = $"pane-{windowIndex}-{paneIndex}",
            Program = @"C:\Windows\System32\cmd.exe",
            Args = [$"/k echo {windowIndex}:{paneIndex}"],
            EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WINMUX_SCALE"] = $"{windowIndex}:{paneIndex}",
            },
            Cwd = new WorkingDirectory(
                $@"C:\work\{windowIndex}\{paneIndex}",
                CwdSource.ShellReported,
                DateTimeOffset.UnixEpoch.AddSeconds(windowIndex * 64 + paneIndex)),
        });
}
