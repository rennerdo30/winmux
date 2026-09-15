using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Core.Layout;
using WinMux.Core.Model;

// Avalonia has a TabStrip control of its own, and it is not this. Ours is a rectangle the layout
// engine reserved; theirs is a widget. Aliased so the two can never be confused silently.
using LayoutTabStrip = WinMux.Core.Layout.TabStrip;

namespace WinMux.Shell.Chrome;

/// <summary>What a tab strip can ask the shell to do. The strip itself owns no state.</summary>
/// <param name="Activate">Bring a tab forward and focus it.</param>
/// <param name="Rename">Give the tab's pane a name of the user's choosing.</param>
/// <param name="CloseTab">Close a tab, by the same path as closing any pane.</param>
/// <param name="AddTab">Add a tab to this stack.</param>
/// <param name="MoveStrip">Put this stack's tabs on a different edge.</param>
internal sealed record TabStripCommands(
    Action<PaneId> Activate,
    Action<PaneId> Rename,
    Action<PaneId> CloseTab,
    Action<StackNode> AddTab,
    Action<StackNode, TabStripPlacement> MoveStrip);

/// <summary>
/// One stack's tabs, drawn in the band the layout engine reserved for them.
///
/// There is one of these per stack rather than one per window, which is what makes a tabbed pane
/// inside a split — or inside another tabbed pane — something you can see and click. The strip
/// never draws outside its own rectangle, because that rectangle was carved out of the stack's
/// space before any pane was placed: a pane hosting a native window paints above everything
/// Avalonia draws (CLAUDE.md section 6), so overlapping would mean tabs that vanish behind Explorer.
/// </summary>
internal static class TabStripView
{
    private const double AccentWeight = 2;

    public static Control Build(LayoutTabStrip strip, PaneId focused, TabStripCommands commands)
    {
        var vertical = strip.IsVertical;
        var items = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = vertical ? 2 : 1,
        };

        // Does focus live anywhere inside this stack? With several strips on screen, "which tabs do
        // my keys act on" stops being obvious, so the focused one is marked.
        var stackHasFocus = strip.Stack.Leaves().Any(leaf => leaf.Pane.Id == focused);

        for (var index = 0; index < strip.Stack.Children.Count; index++)
        {
            var child = strip.Stack.Children[index];
            items.Children.Add(BuildTab(
                child,
                isActive: index == strip.Stack.ActiveIndex,
                hasFocus: stackHasFocus && child.Leaves().Any(leaf => leaf.Pane.Id == focused),
                vertical,
                strip.Placement,
                commands));
        }

        items.Children.Add(Icon(Icons.Add(), "Add a tab to this group", () => commands.AddTab(strip.Stack), vertical));
        items.Children.Add(PlacementButton(strip, commands, vertical));

        var scroller = new ScrollViewer
        {
            Content = items,
            HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Padding = vertical ? new Thickness(6, 6) : new Thickness(6, 0),
        };

        return new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = ContentEdge(strip.Placement, stackHasFocus),
            Child = scroller,
        };
    }

    /// <summary>
    /// A hairline on the side facing the content. Always one pixel and always the edge colour: a
    /// full-width accent slab under the focused strip shouted far louder than Windows ever does,
    /// and the focused tab's own accent already says which group has focus.
    /// </summary>
    private static Thickness ContentEdge(TabStripPlacement placement, bool focused)
    {
        const double weight = 1;
        return placement switch
        {
            TabStripPlacement.Top => new Thickness(0, 0, 0, weight),
            TabStripPlacement.Bottom => new Thickness(0, weight, 0, 0),
            TabStripPlacement.Left => new Thickness(0, 0, weight, 0),
            _ => new Thickness(weight, 0, 0, 0),
        };
    }

    private static Control BuildTab(
        LayoutNode child,
        bool isActive,
        bool hasFocus,
        bool vertical,
        TabStripPlacement placement,
        TabStripCommands commands)
    {
        var leaves = child.Leaves().ToArray();
        var target = leaves[0].Pane.Id;

        // A tab may hold a whole split, not just one pane. Saying so beats showing the first title
        // and quietly implying the other panes are not there.
        var title = leaves.Length == 1
            ? leaves[0].Pane.Title
            : $"{leaves[0].Pane.Title}  +{leaves.Length - 1}";
        if (string.IsNullOrWhiteSpace(title)) title = "untitled";

        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = vertical ? 118 : 170,
            FontSize = Palette.BodySize,
        };

        var close = new Button
        {
            Content = Icons.Close(10),
            Margin = new Thickness(6, 0, -2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = "Close this tab",
        };
        close.Classes.Add(Theme.CloseButton);
        close.Click += (_, e) => { e.Handled = true; commands.CloseTab(target); };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        row.Children.Add(label);

        var tab = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            [ToolTip.TipProperty] = title,
        };
        tab.Classes.Add(Theme.Tab);
        if (isActive) tab.Classes.Add(Theme.ActiveTab);
        tab.Click += (_, _) => commands.Activate(target);

        // Double-click to rename is the convention every tabbed application uses, and it costs
        // nothing: the first click of the pair has already activated the tab.
        tab.DoubleTapped += (_, e) => { e.Handled = true; commands.Rename(target); };

        tab.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item("Rename\u2026", () => commands.Rename(target)),
                Item("Close tab", () => commands.CloseTab(target)),
            },
        };

        // The accent strip is added to *every* tab, transparent when inactive. Adding it only to the
        // active one made that tab two pixels shorter than its neighbours, so the row visibly
        // shifted as the selection moved.
        var accent = new Border
        {
            Background = isActive
                ? (hasFocus ? Palette.AccentBrush : Palette.MutedTextBrush)
                : Brushes.Transparent,
            Opacity = isActive && !hasFocus ? 0.5 : 1,
        };

        var stacked = new DockPanel { LastChildFill = true };
        switch (placement)
        {
            case TabStripPlacement.Top:
                accent.Height = AccentWeight;
                DockPanel.SetDock(accent, Dock.Bottom);
                break;
            case TabStripPlacement.Bottom:
                accent.Height = AccentWeight;
                DockPanel.SetDock(accent, Dock.Top);
                break;
            case TabStripPlacement.Left:
                accent.Width = AccentWeight;
                DockPanel.SetDock(accent, Dock.Right);
                break;
            default:
                accent.Width = AccentWeight;
                DockPanel.SetDock(accent, Dock.Left);
                break;
        }
        stacked.Children.Add(accent);
        stacked.Children.Add(tab);
        return stacked;
    }

    private static Control PlacementButton(LayoutTabStrip strip, TabStripCommands commands, bool vertical)
    {
        var button = new Button
        {
            Content = Icons.More(),
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            [ToolTip.TipProperty] = "Where these tabs sit",
        };
        button.Classes.Add(Theme.IconButton);

        var menu = new ContextMenu
        {
            ItemsSource = new[]
            {
                PlacementItem(strip, commands, TabStripPlacement.Top, "Tabs on top"),
                PlacementItem(strip, commands, TabStripPlacement.Bottom, "Tabs on the bottom"),
                PlacementItem(strip, commands, TabStripPlacement.Left, "Tabs on the left"),
                PlacementItem(strip, commands, TabStripPlacement.Right, "Tabs on the right"),
            },
        };

        // Opens on a plain left click too: a menu reachable only by right-clicking a three-dot
        // button is a menu most people never find.
        button.Click += (_, _) => { menu.PlacementTarget = button; menu.Open(button); };
        button.ContextMenu = menu;
        return button;
    }

    private static MenuItem PlacementItem(
        LayoutTabStrip strip,
        TabStripCommands commands,
        TabStripPlacement placement,
        string header)
    {
        var item = Item(header, () => commands.MoveStrip(strip.Stack, placement));
        // Geometry, not a font glyph: a machine without the font renders a tick as a hollow box,
        // which in a menu of four placements says the wrong one is selected (Icons.cs).
        if (strip.Placement == placement) item.Icon = Icons.Check();
        return item;
    }

    private static Control Icon(Control glyph, string tip, Action invoke, bool vertical)
    {
        var button = new Button
        {
            Content = glyph,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            [ToolTip.TipProperty] = tip,
        };
        button.Classes.Add(Theme.IconButton);
        button.Click += (_, _) => invoke();
        return button;
    }

    private static MenuItem Item(string header, Action invoke)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => invoke();
        return item;
    }
}
