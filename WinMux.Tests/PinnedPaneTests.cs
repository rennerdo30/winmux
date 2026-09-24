using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

/// <summary>
/// Pinning a pane, and the part of it that belongs to priority 1: a pin the user set has to be
/// there again after a restart, or it is a decoration rather than a protection.
/// </summary>
public class PinnedPaneTests
{
    private static SessionSnapshot Snapshot(LayoutTree tree) => new()
    {
        SavedAt = DateTimeOffset.UtcNow,
        Windows = [SessionMapper.ToSnapshot(tree, "pins")],
    };

    [Fact]
    public void A_pin_survives_the_round_trip()
    {
        var kept = Pane.Terminal("build");
        var ordinary = Pane.Terminal("scratch");
        var tree = new LayoutTree(kept);
        tree.AddTab(kept.Id, ordinary);
        kept.IsPinned = true;

        var loaded = SessionFile.Deserialize(SessionFile.Serialize(Snapshot(tree)));
        var restored = SessionMapper.FromSnapshot(loaded.Windows[0]);

        Assert.True(restored.Panes.Single(p => p.Title == "build").IsPinned);
        Assert.False(restored.Panes.Single(p => p.Title == "scratch").IsPinned);
    }

    [Fact]
    public void An_unpinned_pane_writes_no_key_at_all()
    {
        // The panes table is the part of the file meant to be read and hand-edited, and a
        // `pinned = false` on every entry would be noise in it — the same reason title_custom is
        // only written when it is true.
        var text = SessionFile.Serialize(Snapshot(new LayoutTree(Pane.Terminal("one"))));

        Assert.DoesNotContain("pinned", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_written_before_pinning_existed_loads_unpinned()
    {
        var text = SessionFile.Serialize(Snapshot(new LayoutTree(Pane.Terminal("one"))));

        var restored = SessionMapper.FromSnapshot(SessionFile.Deserialize(text).Windows[0]);

        Assert.False(restored.Panes.Single().IsPinned);
    }

    [Fact]
    public void Pinning_does_not_stop_the_tree_from_removing_the_pane()
    {
        // The refusal belongs to the shell's close action, where there is someone to tell. The
        // layout engine stays a pure data structure: a rule buried in it would also block restore,
        // session loading and anything else that rebuilds a tree.
        var pinned = Pane.Terminal("build");
        var other = Pane.Terminal("scratch");
        var tree = new LayoutTree(pinned);
        tree.AddTab(pinned.Id, other);
        pinned.IsPinned = true;

        Assert.True(tree.Close(pinned.Id));
    }
}
