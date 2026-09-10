using WinMux.Core.Model;

namespace WinMux.Core.Layout;

/// <summary>
/// How a split arranges its children.
///
/// Deliberately NOT "Horizontal"/"Vertical": every terminal multiplexer disagrees about what those
/// mean (tmux's `split -h` produces a *vertical* divider). Naming the arrangement rather than the
/// divider removes the ambiguity — <see cref="Columns"/> puts children side by side.
/// </summary>
public enum SplitDirection
{
    /// <summary>Children left to right. The dividers are vertical lines.</summary>
    Columns,

    /// <summary>Children top to bottom. The dividers are horizontal lines.</summary>
    Rows,
}

public enum FocusDirection { Left, Right, Up, Down }

public abstract class LayoutNode
{
    /// <summary>Null only for the root. Maintained by the containers, never set by callers.</summary>
    public LayoutNode? Parent { get; internal set; }

    /// <summary>Every leaf beneath this node, including those hidden in inactive stack tabs.</summary>
    public abstract IEnumerable<LeafNode> Leaves();

    /// <summary>Deep copy, with parents rewired. Used for snapshots and undo.</summary>
    public abstract LayoutNode Clone();

    public bool IsRoot => Parent is null;

    /// <summary>Walk up to the root.</summary>
    public LayoutNode Root()
    {
        var n = this;
        while (n.Parent is not null) n = n.Parent;
        return n;
    }
}

public sealed class LeafNode : LayoutNode
{
    public Pane Pane { get; }

    public LeafNode(Pane pane) => Pane = pane ?? throw new ArgumentNullException(nameof(pane));

    public override IEnumerable<LeafNode> Leaves() { yield return this; }

    public override LayoutNode Clone() => new LeafNode(Pane);

    public override string ToString() => $"Leaf({Pane.Id})";
}

/// <summary>
/// A split with two or more children and a ratio per child.
///
/// Invariants, enforced here rather than trusted: at least two children, one ratio per child,
/// every ratio strictly positive, and the ratios summing to 1.
/// </summary>
public sealed class SplitNode : LayoutNode
{
    private readonly List<LayoutNode> _children = [];
    private readonly List<double> _ratios = [];

    public SplitDirection Direction { get; internal set; }
    public IReadOnlyList<LayoutNode> Children => _children;
    public IReadOnlyList<double> Ratios => _ratios;

    public SplitNode(SplitDirection direction, IEnumerable<LayoutNode> children, IEnumerable<double>? ratios = null)
    {
        Direction = direction;
        foreach (var c in children) { c.Parent = this; _children.Add(c); }
        if (_children.Count < 2)
            throw new ArgumentException("A split needs at least two children; use the child directly instead.", nameof(children));

        if (ratios is null) _ratios.AddRange(Enumerable.Repeat(1.0 / _children.Count, _children.Count));
        else _ratios.AddRange(ratios);

        if (_ratios.Count != _children.Count)
            throw new ArgumentException($"Got {_ratios.Count} ratios for {_children.Count} children.", nameof(ratios));
        Normalize();
    }

    internal void InsertChild(int index, LayoutNode child, double ratio)
    {
        child.Parent = this;
        _children.Insert(index, child);
        _ratios.Insert(index, Math.Max(ratio, MinRatio));
        Normalize();
    }

    internal void RemoveChildAt(int index)
    {
        _children[index].Parent = null;
        _children.RemoveAt(index);
        _ratios.RemoveAt(index);
        if (_ratios.Count > 0) Normalize();
    }

    internal void ReplaceChildAt(int index, LayoutNode child)
    {
        _children[index].Parent = null;
        child.Parent = this;
        _children[index] = child;
    }

    internal void SetRatios(IEnumerable<double> ratios)
    {
        var list = ratios.ToList();
        if (list.Count != _children.Count)
            throw new ArgumentException($"Got {list.Count} ratios for {_children.Count} children.", nameof(ratios));
        _ratios.Clear();
        _ratios.AddRange(list);
        Normalize();
    }

    /// <summary>A pane may not be resized out of existence; this is the floor for any one child.</summary>
    public const double MinRatio = 0.02;

    private void Normalize()
    {
        for (int i = 0; i < _ratios.Count; i++)
            if (double.IsNaN(_ratios[i]) || _ratios[i] < MinRatio) _ratios[i] = MinRatio;

        var sum = _ratios.Sum();
        if (sum <= 0)
        {
            for (int i = 0; i < _ratios.Count; i++) _ratios[i] = 1.0 / _ratios.Count;
            return;
        }
        for (int i = 0; i < _ratios.Count; i++) _ratios[i] /= sum;
    }

    public int IndexOf(LayoutNode child) => _children.IndexOf(child);

    public override IEnumerable<LeafNode> Leaves() => _children.SelectMany(c => c.Leaves());

    public override LayoutNode Clone() => new SplitNode(Direction, _children.Select(c => c.Clone()), _ratios);

    public override string ToString() => $"Split({Direction}, {_children.Count})";
}

/// <summary>
/// A stack of tabs. Exactly one child is visible at a time.
///
/// Invariants: at least one child, and <see cref="ActiveIndex"/> always in range.
/// </summary>
public sealed class StackNode : LayoutNode
{
    private readonly List<LayoutNode> _children = [];
    private int _activeIndex;

    public IReadOnlyList<LayoutNode> Children => _children;

    public int ActiveIndex
    {
        get => _activeIndex;
        internal set => _activeIndex = _children.Count == 0 ? 0 : Math.Clamp(value, 0, _children.Count - 1);
    }

    public LayoutNode Active => _children[_activeIndex];

    public StackNode(IEnumerable<LayoutNode> children, int activeIndex = 0)
    {
        foreach (var c in children) { c.Parent = this; _children.Add(c); }
        if (_children.Count == 0)
            throw new ArgumentException("A stack needs at least one child.", nameof(children));
        ActiveIndex = activeIndex;
    }

    internal void InsertChild(int index, LayoutNode child)
    {
        child.Parent = this;
        _children.Insert(index, child);
        if (index <= _activeIndex && _children.Count > 1) _activeIndex++;
        ActiveIndex = _activeIndex;
    }

    internal void RemoveChildAt(int index)
    {
        _children[index].Parent = null;
        _children.RemoveAt(index);
        if (_children.Count == 0) { _activeIndex = 0; return; }
        if (index < _activeIndex) _activeIndex--;
        ActiveIndex = _activeIndex;
    }

    internal void ReplaceChildAt(int index, LayoutNode child)
    {
        _children[index].Parent = null;
        child.Parent = this;
        _children[index] = child;
    }

    public int IndexOf(LayoutNode child) => _children.IndexOf(child);

    public override IEnumerable<LeafNode> Leaves() => _children.SelectMany(c => c.Leaves());

    public override LayoutNode Clone() => new StackNode(_children.Select(c => c.Clone()), _activeIndex);

    public override string ToString() => $"Stack({_children.Count}, active={_activeIndex})";
}
