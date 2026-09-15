using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

/// <summary>
/// Where a stack keeps its tabs is a layout decision the user made, so priority 1 applies to it:
/// if it does not survive save and restore, the feature is decoration.
/// </summary>
public class TabStripPersistenceTests
{
    private static Pane P(string t) => Pane.Terminal(t);

    [Theory]
    [InlineData(TabStripPlacement.Top)]
    [InlineData(TabStripPlacement.Bottom)]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void Tab_placement_survives_a_round_trip(TabStripPlacement placement)
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));
        Stack(tree.Root).TabStrip = placement;

        var restored = RoundTrip(tree);

        Assert.Equal(placement, Stack(restored.Root).TabStrip);
    }

    [Fact]
    public void Each_stack_keeps_its_own_placement()
    {
        // Placement is per stack, not per window. A file that collapsed them to one value would
        // pass a single-stack round trip and quietly destroy every real layout.
        var left = P("left");
        var right = P("right");
        var tree = new LayoutTree(left) { Bounds = new Rect(0, 0, 800, 600) };
        tree.Split(left.Id, SplitDirection.Columns, right);
        tree.AddTab(left.Id, P("left tab"));
        tree.AddTab(right.Id, P("right tab"));

        var split = (SplitNode)tree.Root;
        ((StackNode)split.Children[0]).TabStrip = TabStripPlacement.Left;
        ((StackNode)split.Children[1]).TabStrip = TabStripPlacement.Bottom;

        var restoredSplit = (SplitNode)RoundTrip(tree).Root;

        Assert.Equal(TabStripPlacement.Left, ((StackNode)restoredSplit.Children[0]).TabStrip);
        Assert.Equal(TabStripPlacement.Bottom, ((StackNode)restoredSplit.Children[1]).TabStrip);
    }

    [Fact]
    public void The_default_placement_is_not_written_at_all()
    {
        // The session file is meant to be hand-editable (ADR 0006). Every stack carrying
        // tabs = "top" would be noise in the file that matters most to read.
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));

        Assert.DoesNotContain("tabs", Serialize(tree), StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_written_before_tab_placement_existed_still_loads()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));
        Stack(tree.Root).TabStrip = TabStripPlacement.Right;

        // Strip the key back out, which is exactly what an older WinMux would have written.
        var withoutKey = string.Join(
            '\n',
            Serialize(tree).Split('\n').Where(line => !line.TrimStart().StartsWith("tabs", StringComparison.Ordinal)));

        var restored = SessionFile.Deserialize(withoutKey);

        Assert.Equal(TabStripPlacement.Top, Stack(SessionMapper.FromSnapshot(restored.Windows[0]).Root).TabStrip);
    }

    [Fact]
    public void A_misspelled_placement_names_the_offender_rather_than_defaulting()
    {
        // "No silent failure around persistence" (CLAUDE.md section 8). Quietly reading "left" as
        // top would move the user's tabs and never say why.
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));
        Stack(tree.Root).TabStrip = TabStripPlacement.Left;

        // Values are TOML literal strings, single-quoted (ADR 0006).
        var broken = Serialize(tree).Replace("'left'", "'sideways'", StringComparison.Ordinal);

        var error = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(broken));

        Assert.Contains("sideways", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"left\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placement_of_the_wrong_type_is_refused_too()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));
        Stack(tree.Root).TabStrip = TabStripPlacement.Left;

        var broken = Serialize(tree).Replace("= 'left'", "= 3", StringComparison.Ordinal);
        Assert.DoesNotContain("'left'", broken, StringComparison.Ordinal);

        var error = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(broken));

        Assert.Contains("should be a string", error.Message, StringComparison.Ordinal);
    }

    private static string Serialize(LayoutTree tree) => SessionFile.Serialize(new SessionSnapshot
    {
        SavedAt = DateTimeOffset.UtcNow,
        Windows = [SessionMapper.ToSnapshot(tree, "w")],
    });

    private static LayoutTree RoundTrip(LayoutTree tree) =>
        SessionMapper.FromSnapshot(SessionFile.Deserialize(Serialize(tree)).Windows[0]);

    private static StackNode Stack(LayoutNode node) => node switch
    {
        StackNode stack => stack,
        SplitNode split => split.Children.Select(Stack).First(),
        _ => throw new InvalidOperationException("No stack in this tree."),
    };
}
