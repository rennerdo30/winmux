using WinMux.Core.Model;

namespace WinMux.Core.Layout;

/// <summary>A draggable divider between two children of a split.</summary>
public readonly record struct Divider(SplitNode Split, int BeforeIndex, Rect Rect, SplitDirection Direction);

/// <summary>
/// Where a stack's tabs are to be drawn — a band reserved out of the stack's own rectangle, at the
/// stack's own position in the tree.
///
/// Emitting these from the layout engine rather than the shell is what makes nested tabbing work
/// at all. A single tab bar at the top of the window can only ever describe one stack; a tree with
/// three stacks in three different corners needs three strips, each where its stack actually is.
/// </summary>
/// <param name="Stack">The stack these tabs belong to.</param>
/// <param name="Rect">The reserved band. Never overlaps any pane rectangle.</param>
/// <param name="Placement">Which edge it occupies, and so whether it reads across or down.</param>
public readonly record struct TabStrip(StackNode Stack, Rect Rect, TabStripPlacement Placement)
{
    /// <summary>True for <see cref="TabStripPlacement.Left"/> and <see cref="TabStripPlacement.Right"/>.</summary>
    public bool IsVertical => Placement is TabStripPlacement.Left or TabStripPlacement.Right;
}

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

    /// <summary>One per visible stack, in tree order. Empty when nothing is tabbed.</summary>
    public IReadOnlyList<TabStrip> TabStrips { get; }

    public Rect Bounds { get; }

    internal Arrangement(Rect bounds, Dictionary<PaneId, Rect> rects, List<Divider> dividers, List<TabStrip> tabStrips)
    {
        Bounds = bounds;
        PaneRects = rects;
        Dividers = dividers;
        TabStrips = tabStrips;
    }

    public Rect this[PaneId id] => PaneRects.TryGetValue(id, out var r) ? r : Rect.Empty;
    public bool IsVisible(PaneId id) => PaneRects.ContainsKey(id);
}

/// <summary>
/// The sizes the layout engine needs but cannot know.
///
/// These exist because the same engine has to serve a pixel surface and a character grid: the CLI
/// renders layouts into a terminal, where a six-column gutter and a 28-row tab strip would be
/// absurd. Passed explicitly rather than held in ambient state — layout is called from more than
/// one thread, and a hidden setting would be a race.
/// </summary>
/// <param name="DividerThickness">Gap between siblings, and the divider's hit target.</param>
/// <param name="HorizontalTabStrip">Height of a top or bottom tab strip.</param>
/// <param name="VerticalTabStrip">
/// Width of a left or right tab strip. Much larger than the horizontal one on purpose: a vertical
/// strip lists titles down the side, so it needs room to read them, and a 28-pixel column of
/// truncated text would be the feature in name only.
/// </param>
public readonly record struct LayoutMetrics(int DividerThickness, int HorizontalTabStrip, int VerticalTabStrip)
{
    /// <summary>What the shell uses: a pixel surface at 100% scale.</summary>
    public static readonly LayoutMetrics Default = new(6, 40, 220);

    /// <summary>A character grid. Tabs are listed in text instead, so no strip is reserved.</summary>
    public static readonly LayoutMetrics CharacterGrid = new(1, 0, 0);

    internal int StripFor(TabStripPlacement placement) =>
        Math.Max(0, placement is TabStripPlacement.Left or TabStripPlacement.Right
            ? VerticalTabStrip
            : HorizontalTabStrip);
}

public static class Layouter
{
    /// <summary>Width of the gap between siblings, in pixels. Also the divider hit target.</summary>
    public const int DividerThickness = 6;

    /// <summary>
    /// Height of a top or bottom tab strip, in pixels.
    ///
    /// Sized from what has to fit rather than picked: a 12px label needs about 16px of line box,
    /// plus the tab's vertical padding, its accent strip and the strip's own border. 28 was too
    /// small for that and clipped every label along its baseline.
    /// </summary>
    public const int TabStripThickness = 40;

    /// <summary>Width of a left or right tab strip, in pixels.</summary>
    public const int VerticalTabStripThickness = 220;

    /// <summary>Content a stack must keep for its tab strip to be worth reserving at all.</summary>
    private const int MinimumContentExtent = 8;

    /// <summary>Lay the tree out inside <paramref name="bounds"/>.</summary>
    public static Arrangement Arrange(LayoutNode root, Rect bounds, LayoutMetrics? metrics = null)
    {
        var m = metrics ?? LayoutMetrics.Default;
        var rects = new Dictionary<PaneId, Rect>();
        var dividers = new List<Divider>();
        var strips = new List<TabStrip>();
        Place(root, bounds, rects, dividers, strips, Math.Max(0, m.DividerThickness), m);
        return new Arrangement(bounds, rects, dividers, strips);
    }

    private static void Place(
        LayoutNode node,
        Rect rect,
        Dictionary<PaneId, Rect> rects,
        List<Divider> dividers,
        List<TabStrip> strips,
        int gutter,
        LayoutMetrics metrics)
    {
        switch (node)
        {
            case LeafNode leaf:
                rects[leaf.Pane.Id] = rect;
                return;

            case StackNode stack:
                PlaceStack(stack, rect, rects, dividers, strips, gutter, metrics);
                return;

            case SplitNode split:
                PlaceSplit(split, rect, rects, dividers, strips, gutter, metrics);
                return;

            default:
                throw new NotSupportedException($"Unknown node type {node.GetType().Name}");
        }
    }

    private static void PlaceStack(
        StackNode stack,
        Rect rect,
        Dictionary<PaneId, Rect> rects,
        List<Divider> dividers,
        List<TabStrip> strips,
        int gutter,
        LayoutMetrics metrics)
    {
        // Reserve the strip out of the stack's own rectangle before placing the active child, so
        // the child's rect and the strip's rect can never overlap. A pane hosting a native window
        // would otherwise paint straight over the tabs that control it.
        var (stripRect, contentRect) = Split(rect, stack.TabStrip, metrics.StripFor(stack.TabStrip));
        if (!stripRect.IsEmpty) strips.Add(new TabStrip(stack, stripRect, stack.TabStrip));

        // Tabs: only the active child occupies the content. The others are not merely hidden,
        // they have no geometry, and asking for their rect is a bug worth surfacing.
        Place(stack.Active, contentRect, rects, dividers, strips, gutter, metrics);
    }

    /// <summary>Carve a band of <paramref name="thickness"/> off the named edge.</summary>
    private static (Rect Strip, Rect Content) Split(Rect rect, TabStripPlacement placement, int thickness)
    {
        // Full thickness or nothing. A strip squeezed to a sliver is unusable but still steals the
        // space a pane needed, so a stack with no room loses its tabs rather than its panes — the
        // panes are what the user is looking at, and a too-small strip is recoverable by resizing
        // while a pane that has quietly shrunk to nothing is not.
        var vertical = placement is TabStripPlacement.Left or TabStripPlacement.Right;
        var available = vertical ? rect.Width : rect.Height;
        var taken = available - thickness >= MinimumContentExtent ? thickness : 0;
        if (taken <= 0) return (Rect.Empty, rect);

        return placement switch
        {
            TabStripPlacement.Top => (
                rect with { Height = taken },
                new Rect(rect.X, rect.Y + taken, rect.Width, rect.Height - taken)),
            TabStripPlacement.Bottom => (
                new Rect(rect.X, rect.Bottom - taken, rect.Width, taken),
                rect with { Height = rect.Height - taken }),
            TabStripPlacement.Left => (
                rect with { Width = taken },
                new Rect(rect.X + taken, rect.Y, rect.Width - taken, rect.Height)),
            TabStripPlacement.Right => (
                new Rect(rect.Right - taken, rect.Y, taken, rect.Height),
                rect with { Width = rect.Width - taken }),
            _ => throw new NotSupportedException($"Unknown tab strip placement {placement}"),
        };
    }

    private static void PlaceSplit(
        SplitNode split,
        Rect rect,
        Dictionary<PaneId, Rect> rects,
        List<Divider> dividers,
        List<TabStrip> strips,
        int gutter,
        LayoutMetrics metrics)
    {
        int n = split.Children.Count;
        bool columns = split.Direction == SplitDirection.Columns;

        int extent = columns ? rect.Width : rect.Height;
        int gaps = (n - 1) * gutter;
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

            Place(split.Children[i], childRect, rects, dividers, strips, gutter, metrics);

            used += size;
            offset += size;

            if (i < n - 1)
            {
                var divRect = columns
                    ? new Rect(offset, rect.Y, gutter, rect.Height)
                    : new Rect(rect.X, offset, rect.Width, gutter);
                dividers.Add(new Divider(split, i, divRect, split.Direction));
                offset += gutter;
            }
        }
    }
}
