using WinMux.Core.Model;

namespace WinMux.Core.Layout;

/// <summary>
/// The layout engine: a tree of splits, stacks and leaves, plus every operation the keymap exposes.
///
/// Pure logic, no platform types, and canonical by construction — after any operation there is
/// never a split or a stack with fewer than two children. Canonicalising eagerly is what keeps
/// serialization round-trips and structural comparisons honest; the alternative is a tree that
/// compares unequal to itself after a close-and-reopen.
/// </summary>
public sealed class LayoutTree
{
    private LayoutNode _root;
    private PaneId _focused;

    /// <summary>
    /// The area the tree is laid out into. Set by the shell on window resize. Focus movement is
    /// geometric, so it needs this; with empty bounds directional focus does nothing.
    /// </summary>
    public Rect Bounds { get; set; } = Rect.Empty;

    public LayoutNode Root => _root;
    public PaneId Focused => _focused;

    public LayoutTree(Pane first)
    {
        var leaf = new LeafNode(first);
        _root = leaf;
        _focused = first.Id;
    }

    public LayoutTree(LayoutNode root, PaneId focused)
    {
        _root = root;
        _root.Parent = null;
        _focused = focused;
        if (Find(focused) is null) _focused = _root.Leaves().First().Pane.Id;
    }

    // ---------------- queries ----------------

    public IEnumerable<Pane> Panes => _root.Leaves().Select(l => l.Pane);

    public LeafNode? Find(PaneId id) => _root.Leaves().FirstOrDefault(l => l.Pane.Id == id);

    public Pane? GetPane(PaneId id) => Find(id)?.Pane;

    public Arrangement Arrange() => Layouter.Arrange(_root, Bounds);
    public Arrangement Arrange(Rect bounds) { Bounds = bounds; return Layouter.Arrange(_root, bounds); }

    /// <summary>Panes not hidden behind an inactive tab.</summary>
    public IEnumerable<Pane> VisiblePanes()
    {
        var seen = new List<Pane>();
        Walk(_root);
        return seen;

        void Walk(LayoutNode n)
        {
            switch (n)
            {
                case LeafNode leaf: seen.Add(leaf.Pane); break;
                case StackNode s: Walk(s.Active); break;
                case SplitNode sp: foreach (var c in sp.Children) Walk(c); break;
            }
        }
    }

    // ---------------- structural operations ----------------

    /// <summary>
    /// Split the pane in two, inserting <paramref name="newPane"/> after it.
    /// <paramref name="ratio"/> is the share the NEW pane receives.
    /// </summary>
    public PaneId Split(PaneId target, SplitDirection direction, Pane newPane, double ratio = 0.5)
    {
        var leaf = Find(target) ?? throw new InvalidOperationException($"No such pane: {target}");
        var incoming = new LeafNode(newPane);
        ratio = Math.Clamp(ratio, SplitNode.MinRatio, 1 - SplitNode.MinRatio);

        // Growing an existing split in the same direction keeps the tree flat, which is what a
        // user means by "split again the same way" — nesting would make ratios behave oddly.
        if (leaf.Parent is SplitNode parent && parent.Direction == direction)
        {
            int idx = parent.IndexOf(leaf);
            double share = parent.Ratios[idx];
            parent.InsertChild(idx + 1, incoming, share * ratio);
            var ratios = parent.Ratios.ToArray();
            ratios[idx] = share * (1 - ratio);
            parent.SetRatios(ratios);
        }
        else
        {
            var split = new SplitNode(direction, [new LeafNode(leaf.Pane), incoming], [1 - ratio, ratio]);
            Replace(leaf, split);
        }

        _focused = newPane.Id;
        return newPane.Id;
    }

    /// <summary>Add a tab beside the target pane, wrapping it in a stack if it is not already in one.</summary>
    public PaneId AddTab(PaneId target, Pane newPane)
    {
        var leaf = Find(target) ?? throw new InvalidOperationException($"No such pane: {target}");
        var incoming = new LeafNode(newPane);

        if (leaf.Parent is StackNode stack)
        {
            int idx = stack.IndexOf(leaf);
            stack.InsertChild(idx + 1, incoming);
            stack.ActiveIndex = idx + 1;
        }
        else
        {
            var created = new StackNode([new LeafNode(leaf.Pane), incoming], activeIndex: 1);
            Replace(leaf, created);
        }

        _focused = newPane.Id;
        return newPane.Id;
    }

    /// <summary>
    /// Put the target pane and a new one into a tab group of their own, whatever is around them.
    ///
    /// <para>
    /// Distinct from <see cref="AddTab"/>, which joins the group the pane is already in if there is
    /// one. "Turn this pane into a tab group" and "add a tab beside this pane" are the same thing
    /// exactly once — the first time — and the toolbar button promising the first was doing the
    /// second on every press after that. A pane already in a group gets a group nested inside it,
    /// which is a shape the layout has always supported (ADR 0014).
    /// </para>
    /// </summary>
    public PaneId AddTabGroup(PaneId target, Pane newPane)
    {
        ArgumentNullException.ThrowIfNull(newPane);

        var leaf = Find(target) ?? throw new InvalidOperationException($"No such pane: {target}");
        var created = new StackNode([new LeafNode(leaf.Pane), new LeafNode(newPane)], activeIndex: 1);
        Replace(leaf, created);
        _focused = newPane.Id;
        return newPane.Id;
    }

    /// <summary>
    /// Add a tab to a stack that already exists, beside its active child.
    ///
    /// <para>
    /// Distinct from <see cref="AddTab"/>, which says "beside <em>this pane</em>" and therefore
    /// lands wherever that pane's parent happens to be. A stack's own "+" button means "another tab
    /// <em>here</em>", and the two are not the same thing the moment a stack holds another stack or
    /// a split: naming a pane inside the active child puts the new tab in that child's group rather
    /// than in the one whose button was pressed.
    /// </para>
    /// </summary>
    public PaneId AddTabToStack(StackNode stack, Pane newPane)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(newPane);

        var index = stack.ActiveIndex + 1;
        stack.InsertChild(index, new LeafNode(newPane));
        stack.ActiveIndex = index;
        _focused = newPane.Id;
        return newPane.Id;
    }

    /// <summary>
    /// Remove a pane. Returns false if it does not exist, or if it is the last pane in the tree —
    /// a window always has at least one pane, and closing the last one is the shell's decision,
    /// not the tree's.
    /// </summary>
    public bool Close(PaneId target)
    {
        var leaf = Find(target);
        if (leaf is null) return false;
        if (_root is LeafNode) return false;

        var successor = NeighbourFor(leaf);
        var parent = leaf.Parent!;

        switch (parent)
        {
            case SplitNode split:
                split.RemoveChildAt(split.IndexOf(leaf));
                if (split.Children.Count == 1) Collapse(split, split.Children[0]);
                break;

            case StackNode stack:
                stack.RemoveChildAt(stack.IndexOf(leaf));
                if (stack.Children.Count == 1) Collapse(stack, stack.Children[0]);
                break;
        }

        if (_focused == target)
            _focused = successor ?? _root.Leaves().First().Pane.Id;

        EnsureFocusVisible();
        return true;
    }

    /// <summary>
    /// Move a pane out of wherever it is and into <paramref name="target"/>, at
    /// <paramref name="index"/>.
    ///
    /// <para>
    /// This is a tab dragged from one group onto another. The pane itself is carried across
    /// untouched — the same <see cref="Pane"/>, so the shell's runtime for it keeps running and
    /// nothing is torn down and rebuilt. Where it came from is tidied exactly as closing would tidy
    /// it: a group or a split left holding one child collapses into that child.
    /// </para>
    ///
    /// <para>
    /// Refused, returning false, when the pane is not in the tree, when <paramref name="target"/> is
    /// not in this tree, or when the target sits <em>inside</em> the pane being moved — a node
    /// cannot become its own descendant, and the tree would be a ring rather than a tree.
    /// </para>
    /// </summary>
    public bool MoveTabToStack(PaneId pane, StackNode target, int index)
    {
        ArgumentNullException.ThrowIfNull(target);

        var leaf = Find(pane);
        if (leaf is null || leaf.Parent is null) return false;
        if (!ReferenceEquals(RootOf(target), _root)) return false;

        // Already where it is being asked to go, so removing and reinserting would only renumber
        // the tabs around it.
        var from = target.IndexOf(leaf);
        if (from >= 0 && (index == from || index == from + 1))
        {
            target.ActiveIndex = from;
            _focused = pane;
            return true;
        }

        // The target must survive the removal below. A stack that holds this leaf and nothing else
        // collapses into it, and inserting into a node no longer in the tree loses the pane.
        if (ReferenceEquals(leaf.Parent, target) && target.Children.Count <= 1) return false;
        if (IsWithin(target, leaf)) return false;

        var parent = leaf.Parent;
        switch (parent)
        {
            case SplitNode split:
                split.RemoveChildAt(split.IndexOf(leaf));
                if (split.Children.Count == 1) Collapse(split, split.Children[0]);
                break;

            case StackNode stack:
                stack.RemoveChildAt(stack.IndexOf(leaf));
                if (stack.Children.Count == 1) Collapse(stack, stack.Children[0]);
                break;
        }

        var at = Math.Clamp(index, 0, target.Children.Count);
        target.InsertChild(at, new LeafNode(leaf.Pane));
        target.ActiveIndex = at;
        _focused = pane;
        EnsureFocusVisible();
        return true;
    }

    /// <summary>Whether <paramref name="node"/> is <paramref name="ancestor"/> or sits under it.</summary>
    private static bool IsWithin(LayoutNode node, LayoutNode ancestor)
    {
        for (var walk = node; walk is not null; walk = walk.Parent)
        {
            if (ReferenceEquals(walk, ancestor)) return true;
        }

        return false;
    }

    private static LayoutNode RootOf(LayoutNode node)
    {
        var walk = node;
        while (walk.Parent is { } parent) walk = parent;
        return walk;
    }

    /// <summary>Which pane should take focus when <paramref name="leaf"/> goes away.</summary>
    private PaneId? NeighbourFor(LeafNode leaf)
    {
        if (leaf.Parent is SplitNode split)
        {
            int i = split.IndexOf(leaf);
            var sibling = i + 1 < split.Children.Count ? split.Children[i + 1] : split.Children[i - 1];
            return sibling.Leaves().First().Pane.Id;
        }
        if (leaf.Parent is StackNode stack)
        {
            int i = stack.IndexOf(leaf);
            var sibling = i + 1 < stack.Children.Count ? stack.Children[i + 1] : stack.Children[i - 1];
            return sibling.Leaves().First().Pane.Id;
        }
        return null;
    }

    /// <summary>
    /// Swap one pane for another, in place.
    ///
    /// The layout does not move: the new pane takes the old one's exact position, its share of its
    /// split and its place in any tab group. This is how an empty pane becomes something, and how a
    /// pane whose contents died is replaced without disturbing the arrangement around it.
    /// </summary>
    /// <returns>False when <paramref name="target"/> is not in this tree.</returns>
    public bool ReplacePane(PaneId target, Pane replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (Find(target) is not { } leaf) return false;

        Replace(leaf, new LeafNode(replacement));
        if (_focused == target) _focused = replacement.Id;
        EnsureFocusVisible();
        return true;
    }

    /// <summary>Replace a node with another in its parent, or as the root.</summary>
    private void Replace(LayoutNode existing, LayoutNode replacement)
    {
        var parent = existing.Parent;
        if (parent is null)
        {
            replacement.Parent = null;
            _root = replacement;
            return;
        }
        switch (parent)
        {
            case SplitNode s: s.ReplaceChildAt(s.IndexOf(existing), replacement); break;
            case StackNode st: st.ReplaceChildAt(st.IndexOf(existing), replacement); break;
        }
    }

    /// <summary>Dissolve a container that is down to one child, hoisting the child in its place.</summary>
    private void Collapse(LayoutNode container, LayoutNode onlyChild)
    {
        onlyChild.Parent = null;
        Replace(container, onlyChild);
    }

    // ---------------- resize ----------------

    /// <summary>
    /// Nudge the divider after <paramref name="target"/> within its parent split, moving
    /// <paramref name="delta"/> of the split's extent from the next sibling to this pane.
    /// Returns false when the pane has no split parent, or is the last child with nothing to take from.
    /// </summary>
    public bool Resize(PaneId target, double delta)
    {
        var leaf = Find(target);
        if (leaf?.Parent is not SplitNode split) return false;

        int i = split.IndexOf(leaf);
        int j = i + 1 < split.Children.Count ? i + 1 : i - 1;
        if (j < 0) return false;
        if (j < i) delta = -delta;

        var ratios = split.Ratios.ToArray();
        var moved = Math.Clamp(delta, -(ratios[i] - SplitNode.MinRatio), ratios[j] - SplitNode.MinRatio);
        if (Math.Abs(moved) < 1e-9) return false;

        ratios[i] += moved;
        ratios[j] -= moved;
        split.SetRatios(ratios);
        return true;
    }

    /// <summary>Drag a specific divider, as returned by <see cref="Arrangement.Dividers"/>.</summary>
    public bool ResizeDivider(Divider divider, double delta)
    {
        var split = divider.Split;
        int i = divider.BeforeIndex, j = i + 1;
        if (j >= split.Children.Count) return false;

        var ratios = split.Ratios.ToArray();
        var moved = Math.Clamp(delta, -(ratios[i] - SplitNode.MinRatio), ratios[j] - SplitNode.MinRatio);
        if (Math.Abs(moved) < 1e-9) return false;

        ratios[i] += moved;
        ratios[j] -= moved;
        split.SetRatios(ratios);
        return true;
    }

    /// <summary>Give every child of the target's parent split an equal share.</summary>
    public bool EqualizeSiblings(PaneId target)
    {
        var leaf = Find(target);
        if (leaf?.Parent is not SplitNode split) return false;
        split.SetRatios(Enumerable.Repeat(1.0 / split.Children.Count, split.Children.Count));
        return true;
    }

    // ---------------- focus ----------------

    public bool Focus(PaneId id)
    {
        var leaf = Find(id);
        if (leaf is null) return false;
        _focused = id;
        RevealAncestorTabs(leaf);
        return true;
    }

    /// <summary>Activate whatever tabs are needed to make this node visible.</summary>
    private void RevealAncestorTabs(LayoutNode node)
    {
        var child = node;
        var parent = node.Parent;
        while (parent is not null)
        {
            if (parent is StackNode stack)
            {
                int i = stack.IndexOf(child);
                if (i >= 0) stack.ActiveIndex = i;
            }
            child = parent;
            parent = parent.Parent;
        }
    }

    private void EnsureFocusVisible()
    {
        var leaf = Find(_focused);
        if (leaf is not null) RevealAncestorTabs(leaf);
    }

    /// <summary>
    /// Move focus spatially, the way tmux's select-pane -L/-R/-U/-D does.
    ///
    /// Geometric rather than structural: the tree shape is not what a user sees, so "the pane to
    /// the left" has to be answered from the arranged rectangles. Requires <see cref="Bounds"/>.
    /// </summary>
    public bool MoveFocus(FocusDirection direction)
    {
        if (Bounds.IsEmpty) return false;

        var arrangement = Arrange();
        if (!arrangement.PaneRects.TryGetValue(_focused, out var from)) return false;

        PaneId? best = null;
        var bestPrimary = int.MaxValue;
        var bestSecondary = int.MaxValue;

        foreach (var (id, rect) in arrangement.PaneRects)
        {
            if (id == _focused) continue;

            int primary;      // distance in the direction of travel
            int secondary;    // misalignment on the perpendicular axis

            switch (direction)
            {
                case FocusDirection.Left:
                    if (rect.Right > from.Left || !rect.OverlapsVertically(from)) continue;
                    primary = from.Left - rect.Right;
                    secondary = Math.Abs(rect.CenterY - from.CenterY);
                    break;
                case FocusDirection.Right:
                    if (rect.Left < from.Right || !rect.OverlapsVertically(from)) continue;
                    primary = rect.Left - from.Right;
                    secondary = Math.Abs(rect.CenterY - from.CenterY);
                    break;
                case FocusDirection.Up:
                    if (rect.Bottom > from.Top || !rect.OverlapsHorizontally(from)) continue;
                    primary = from.Top - rect.Bottom;
                    secondary = Math.Abs(rect.CenterX - from.CenterX);
                    break;
                case FocusDirection.Down:
                    if (rect.Top < from.Bottom || !rect.OverlapsHorizontally(from)) continue;
                    primary = rect.Top - from.Bottom;
                    secondary = Math.Abs(rect.CenterX - from.CenterX);
                    break;
                default:
                    continue;
            }

            if (primary < bestPrimary || (primary == bestPrimary && secondary < bestSecondary))
            {
                best = id;
                bestPrimary = primary;
                bestSecondary = secondary;
            }
        }

        if (best is null) return false;
        _focused = best.Value;
        return true;
    }

    /// <summary>Switch tabs in the nearest enclosing stack. Wraps around.</summary>
    public bool CycleTab(int delta)
    {
        var leaf = Find(_focused);
        LayoutNode? child = leaf;
        var parent = leaf?.Parent;
        while (parent is not null && parent is not StackNode) { child = parent; parent = parent.Parent; }
        if (parent is not StackNode stack || child is null) return false;

        int count = stack.Children.Count;
        int next = ((stack.ActiveIndex + delta) % count + count) % count;
        stack.ActiveIndex = next;
        _focused = stack.Active.Leaves().First().Pane.Id;
        return true;
    }

    /// <summary>
    /// Move the focused pane's tab within its stack, without wrapping.
    ///
    /// Not wrapping is deliberate and the opposite of <see cref="CycleTab"/>: cycling past the end
    /// is what you want when *selecting* a tab, and teleporting a tab from one end of the strip to
    /// the other is never what you meant when dragging one.
    /// </summary>
    public bool MoveTab(int delta)
    {
        if (delta == 0) return false;

        var leaf = Find(_focused);
        LayoutNode? child = leaf;
        var parent = leaf?.Parent;
        while (parent is not null && parent is not StackNode) { child = parent; parent = parent.Parent; }
        if (parent is not StackNode stack || child is null) return false;

        var from = stack.IndexOf(child);
        if (from < 0) return false;

        return stack.MoveChild(from, from + delta);
    }

    /// <summary>
    /// Put the tab holding <paramref name="pane"/> at <paramref name="index"/> in its stack.
    ///
    /// Absolute rather than relative because a drop knows where it landed, not how far it came,
    /// and converting one into the other at the call site is how off-by-ones get written twice.
    /// </summary>
    public bool MoveTabTo(PaneId pane, int index)
    {
        var leaf = Find(pane);
        LayoutNode? child = leaf;
        var parent = leaf?.Parent;
        while (parent is not null && parent is not StackNode) { child = parent; parent = parent.Parent; }
        if (parent is not StackNode stack || child is null) return false;

        var from = stack.IndexOf(child);
        return from >= 0 && stack.MoveChild(from, index);
    }

    public LayoutTree Clone() => new(_root.Clone(), _focused) { Bounds = Bounds };
}
