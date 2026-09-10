using WinMux.Core.Model;

namespace WinMux.Core.Layout;

/// <summary>A draggable divider between two children of a split.</summary>
public readonly record struct Divider(SplitNode Split, int BeforeIndex, Rect Rect, SplitDirection Direction);

/// <summary>
/// The computed geometry of a tree within some bounds.
///
/// These are *requested* rectangles. A hosted foreign window will not always honour them — ADR 0003
/// measured a consistent one-pixel error from DPI virtualization, and apps with minimum sizes miss
/// by more — so nothing downstream may assert that an actual rect equals the rect requested here.
/// </summary>
public sealed class Arrangement
{
    /// <summary>Visible panes only. A pane in an inactive stack tab has no rectangle at all.</summary>
    public IReadOnlyDictionary<PaneId, Rect> PaneRects { get; }

    public IReadOnlyList<Divider> Dividers { get; }
    public Rect Bounds { get; }

    internal Arrangement(Rect bounds, Dictionary<PaneId, Rect> rects, List<Divider> dividers)
    {
        Bounds = bounds;
        PaneRects = rects;
        Dividers = dividers;
    }

    public Rect this[PaneId id] => PaneRects.TryGetValue(id, out var r) ? r : Rect.Empty;
    public bool IsVisible(PaneId id) => PaneRects.ContainsKey(id);
}

public static class Layouter
{
    /// <summary>Width of the gap between siblings, in pixels. Also the divider hit target.</summary>
    public const int DividerThickness = 6;

    public static Arrangement Arrange(LayoutNode root, Rect bounds)
    {
        var rects = new Dictionary<PaneId, Rect>();
        var dividers = new List<Divider>();
        Place(root, bounds, rects, dividers);
        return new Arrangement(bounds, rects, dividers);
    }

    private static void Place(LayoutNode node, Rect rect, Dictionary<PaneId, Rect> rects, List<Divider> dividers)
    {
        switch (node)
        {
            case LeafNode leaf:
                rects[leaf.Pane.Id] = rect;
                return;

            case StackNode stack:
                // Tabs: only the active child occupies the rect. The others are not merely hidden,
                // they have no geometry, and asking for their rect is a bug worth surfacing.
                Place(stack.Active, rect, rects, dividers);
                return;

            case SplitNode split:
                PlaceSplit(split, rect, rects, dividers);
                return;

            default:
                throw new NotSupportedException($"Unknown node type {node.GetType().Name}");
        }
    }

    private static void PlaceSplit(SplitNode split, Rect rect, Dictionary<PaneId, Rect> rects, List<Divider> dividers)
    {
        int n = split.Children.Count;
        bool columns = split.Direction == SplitDirection.Columns;

        int extent = columns ? rect.Width : rect.Height;
        int gaps = (n - 1) * DividerThickness;
        int available = Math.Max(0, extent - gaps);

        // Distribute by cumulative rounding rather than per-child rounding: per-child rounding
        // loses or gains pixels as n grows, and the drift is visible as a wobbling divider.
        int used = 0;
        double acc = 0;
        int offset = columns ? rect.X : rect.Y;

        for (int i = 0; i < n; i++)
        {
            int size;
            if (i == n - 1)
            {
                size = available - used;
            }
            else
            {
                acc += split.Ratios[i] * available;
                size = (int)Math.Round(acc) - used;
            }
            size = Math.Max(0, size);

            var childRect = columns
                ? new Rect(offset, rect.Y, size, rect.Height)
                : new Rect(rect.X, offset, rect.Width, size);

            Place(split.Children[i], childRect, rects, dividers);

            used += size;
            offset += size;

            if (i < n - 1)
            {
                var divRect = columns
                    ? new Rect(offset, rect.Y, DividerThickness, rect.Height)
                    : new Rect(rect.X, offset, rect.Width, DividerThickness);
                dividers.Add(new Divider(split, i, divRect, split.Direction));
                offset += DividerThickness;
            }
        }
    }
}
