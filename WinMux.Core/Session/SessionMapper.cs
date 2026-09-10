using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Core.Session;

/// <summary>
/// Converts between the live layout tree and its persisted snapshot.
///
/// This is the round-trip that the whole product rests on (priority 1), so restoring validates
/// rather than trusts: a session file is user data and a malformed one is a bug worth surfacing,
/// never a reason to silently start empty (CLAUDE.md sections 4 and 8).
/// </summary>
public static class SessionMapper
{
    public static WindowSnapshot ToSnapshot(LayoutTree tree, string title = "")
        => new()
        {
            Title = title,
            Bounds = tree.Bounds,
            FocusedPane = tree.Focused.Value,
            Root = ToSnapshot(tree.Root),
        };

    public static NodeSnapshot ToSnapshot(LayoutNode node) => node switch
    {
        LeafNode leaf => new NodeSnapshot
        {
            Kind = NodeKinds.Leaf,
            Pane = new PaneSnapshot
            {
                Id = leaf.Pane.Id.Value,
                Kind = leaf.Pane.Kind,
                Title = leaf.Pane.Title,
                Restore = leaf.Pane.Restore,
            },
        },

        SplitNode split => new NodeSnapshot
        {
            Kind = NodeKinds.Split,
            Direction = split.Direction,
            Ratios = split.Ratios.ToArray(),
            Children = split.Children.Select(ToSnapshot).ToArray(),
        },

        StackNode stack => new NodeSnapshot
        {
            Kind = NodeKinds.Stack,
            ActiveIndex = stack.ActiveIndex,
            Children = stack.Children.Select(ToSnapshot).ToArray(),
        },

        _ => throw new NotSupportedException($"Unknown node type {node.GetType().Name}"),
    };

    public static LayoutTree FromSnapshot(WindowSnapshot snapshot)
    {
        var root = FromSnapshot(snapshot.Root);
        var tree = new LayoutTree(root, new PaneId(snapshot.FocusedPane)) { Bounds = snapshot.Bounds };
        return tree;
    }

    public static LayoutNode FromSnapshot(NodeSnapshot node)
    {
        switch (node.Kind)
        {
            case NodeKinds.Leaf:
            {
                var p = node.Pane ?? throw new SessionFormatException("A leaf node has no pane.");
                var pane = new Pane(new PaneId(p.Id), p.Kind, p.Title, p.Restore);
                return new LeafNode(pane);
            }

            case NodeKinds.Split:
            {
                var children = RequireChildren(node, minimum: 2);
                var direction = node.Direction
                    ?? throw new SessionFormatException("A split node has no direction.");
                var ratios = node.Ratios?.ToArray();
                if (ratios is not null && ratios.Length != children.Count)
                    throw new SessionFormatException(
                        $"A split node has {ratios.Length} ratios for {children.Count} children.");
                return new SplitNode(direction, children, ratios);
            }

            case NodeKinds.Stack:
            {
                var children = RequireChildren(node, minimum: 2);
                return new StackNode(children, node.ActiveIndex ?? 0);
            }

            default:
                throw new SessionFormatException($"Unknown node kind \"{node.Kind}\".");
        }
    }

    private static List<LayoutNode> RequireChildren(NodeSnapshot node, int minimum)
    {
        var raw = node.Children;
        if (raw is null || raw.Count < minimum)
            throw new SessionFormatException(
                $"A {node.Kind} node needs at least {minimum} children, found {raw?.Count ?? 0}. " +
                "The tree is canonical: containers with fewer children are collapsed on save.");
        return raw.Select(FromSnapshot).ToList();
    }

    /// <summary>
    /// Reject a snapshot we cannot honestly restore, with a message that says what to do.
    /// Returns the same snapshot so it can be used inline.
    /// </summary>
    public static SessionSnapshot Validate(SessionSnapshot snapshot)
    {
        if (snapshot.Version <= 0)
            throw new SessionFormatException("Session file has no version. It was not written by WinMux.");
        if (snapshot.Version > SessionSnapshot.CurrentVersion)
            throw new SessionFormatException(
                $"Session file is version {snapshot.Version}, but this build understands up to " +
                $"{SessionSnapshot.CurrentVersion}. Upgrade WinMux rather than letting it discard your layout.");
        if (snapshot.Windows.Count == 0)
            throw new SessionFormatException("Session file contains no windows.");

        foreach (var w in snapshot.Windows) _ = FromSnapshot(w.Root);
        return snapshot;
    }
}
