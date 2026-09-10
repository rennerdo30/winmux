using WinMux.Core.Layout;

namespace WinMux.Core.Session;

/// <summary>One node of the flattened tree, as it appears in a `[[windows.nodes]]` table.</summary>
internal sealed record FlatNode(
    string Id,
    string Kind,
    Guid? PaneId,
    SplitDirection? Direction,
    IReadOnlyList<string> Children,
    IReadOnlyList<double>? Ratios,
    int? ActiveIndex);

internal sealed record FlatTree(string RootId, IReadOnlyList<FlatNode> Nodes, IReadOnlyList<PaneSnapshot> Panes);

/// <summary>
/// Converts between the recursive <see cref="NodeSnapshot"/> and the flat node list the session
/// file uses (ADR 0006).
///
/// Node ids are assigned in pre-order and are file-local — they carry no meaning beyond the file
/// and are regenerated on every save. Assigning them deterministically matters: ids that shuffle
/// between saves would make every session file churn in a diff for no reason.
/// </summary>
internal static class Flattener
{
    public static FlatTree Flatten(NodeSnapshot root)
    {
        var nodes = new List<FlatNode>();
        var panes = new List<PaneSnapshot>();
        var seenPanes = new HashSet<Guid>();
        int next = 0;

        var rootId = Visit(root);
        return new FlatTree(rootId, nodes, panes);

        string Visit(NodeSnapshot node)
        {
            var id = "n" + next++;

            switch (node.Kind)
            {
                case NodeKinds.Leaf:
                {
                    var pane = node.Pane ?? throw new SessionFormatException("A leaf node has no pane.");
                    if (seenPanes.Add(pane.Id)) panes.Add(pane);
                    nodes.Add(new FlatNode(id, NodeKinds.Leaf, pane.Id, null, [], null, null));
                    return id;
                }

                case NodeKinds.Split:
                {
                    var children = node.Children ?? [];
                    // Reserve this node's slot before recursing so ids read top-down in the file.
                    var placeholder = nodes.Count;
                    nodes.Add(null!);
                    var childIds = children.Select(Visit).ToArray();
                    nodes[placeholder] = new FlatNode(
                        id, NodeKinds.Split, null, node.Direction, childIds,
                        node.Ratios?.ToArray() ?? Even(childIds.Length), null);
                    return id;
                }

                case NodeKinds.Stack:
                {
                    var children = node.Children ?? [];
                    var placeholder = nodes.Count;
                    nodes.Add(null!);
                    var childIds = children.Select(Visit).ToArray();
                    nodes[placeholder] = new FlatNode(
                        id, NodeKinds.Stack, null, null, childIds, null, node.ActiveIndex ?? 0);
                    return id;
                }

                default:
                    throw new SessionFormatException($"Unknown node kind \"{node.Kind}\".");
            }
        }

        static double[] Even(int n) => Enumerable.Repeat(1.0 / Math.Max(n, 1), n).ToArray();
    }

    public static NodeSnapshot Rebuild(
        string rootId,
        IReadOnlyDictionary<string, FlatNode> nodes,
        IReadOnlyDictionary<Guid, PaneSnapshot> panes)
    {
        // A session file is user data and may have been hand-edited into a cycle. Following one
        // would hang the app on load, which is a far worse failure than refusing the file.
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        return Build(rootId);

        NodeSnapshot Build(string id)
        {
            if (!nodes.TryGetValue(id, out var node))
                throw new SessionFormatException($"Node \"{id}\" is referenced but not defined.");
            if (!onPath.Add(id))
                throw new SessionFormatException($"The layout tree contains a cycle through node \"{id}\".");

            try
            {
                switch (node.Kind)
                {
                    case NodeKinds.Leaf:
                    {
                        var paneId = node.PaneId
                            ?? throw new SessionFormatException($"Leaf node \"{id}\" names no pane.");
                        if (!panes.TryGetValue(paneId, out var pane))
                            throw new SessionFormatException(
                                $"Leaf node \"{id}\" refers to pane {paneId:D}, which has no [[windows.panes]] entry.");
                        return new NodeSnapshot { Kind = NodeKinds.Leaf, Pane = pane };
                    }

                    case NodeKinds.Split:
                        return new NodeSnapshot
                        {
                            Kind = NodeKinds.Split,
                            Direction = node.Direction
                                ?? throw new SessionFormatException($"Split node \"{id}\" has no direction."),
                            Ratios = node.Ratios,
                            Children = node.Children.Select(Build).ToArray(),
                        };

                    case NodeKinds.Stack:
                        return new NodeSnapshot
                        {
                            Kind = NodeKinds.Stack,
                            ActiveIndex = node.ActiveIndex ?? 0,
                            Children = node.Children.Select(Build).ToArray(),
                        };

                    default:
                        throw new SessionFormatException($"Unknown node kind \"{node.Kind}\" on node \"{id}\".");
                }
            }
            finally
            {
                onPath.Remove(id);
            }
        }
    }
}
