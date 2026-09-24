using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// <param name="Rename">Name what the tab holds — the pane, or the group when it holds one.</param>
/// <param name="CloseTab">Close a tab, by the same path as closing any pane.</param>
/// <param name="AddTab">Add a tab to this stack.</param>
/// <param name="MoveStrip">Put this stack's tabs on a different edge.</param>
internal sealed record TabStripCommands(
    Action<PaneId> Activate,
    Action<LayoutNode> Rename,
    Action<PaneId> CloseTab,
    Action<PaneId> TogglePin,
    Action<PaneId, StackNode, int> MoveTabToGroup,
    Action<StackNode> AddTab,
    Action<StackNode, TabStripPlacement> MoveStrip,
    Action<int> MoveTab);

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

    /// <param name="waiting">
    /// What a pane is waiting to tell the user, or null. A tab whose pane is waiting says so, which
    /// is how "one of these six panes rang" becomes "that one".
    /// </param>
    public static Control Build(
        LayoutTabStrip strip,
        PaneId focused,
        TabStripCommands commands,
        Func<PaneId, string?>? waiting = null)
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

        var tabs = new List<Control>();
        for (var index = 0; index < strip.Stack.Children.Count; index++)
        {
            var child = strip.Stack.Children[index];
            // Any pane under this tab, because a tab can hold a whole split and the pane that rang
            // may not be the one whose title the tab shows.
            var asking = waiting is null
                ? null
                : child.Leaves().Select(leaf => waiting(leaf.Pane.Id)).FirstOrDefault(m => m is not null);

            var tab = BuildTab(
                child,
                isActive: index == strip.Stack.ActiveIndex,
                hasFocus: stackHasFocus && child.Leaves().Any(leaf => leaf.Pane.Id == focused),
                vertical,
                strip.Placement,
                commands,
                asking);

            tabs.Add(tab);
            items.Children.Add(tab);
        }

        EnableDragAndDrop(items, tabs, strip, vertical, commands);

        items.Children.Add(Icon(Icons.Add(), "Add a tab to this group", () => commands.AddTab(strip.Stack), vertical));
        items.Children.Add(PlacementButton(strip, commands, vertical));

        // The caret that shows where a dragged tab will land, drawn over the tabs and inside the
        // same scrolled space so it stays on the gap when the strip is scrolled.
        var caret = new Border
        {
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(1),
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var scroller = new ScrollViewer
        {
            Content = new Panel { Children = { items, caret } },
            HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Padding = vertical ? new Thickness(6, 6) : new Thickness(6, 0),
        };

        var band = new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = ContentEdge(strip.Placement, stackHasFocus),
            Child = scroller,
        };

        // The whole band accepts a drop, not only the tabs in it: a group of one tab has almost no
        // tab to aim at, and dropping into an empty part of the strip is what people try first.
        AcceptDroppedTabs(band, items, caret, tabs, strip, vertical, commands);
        return band;
    }

    /// <summary>
    /// The format the dragged tab travels in: a pane id in its string form.
    ///
    /// An application format, so it is offered to this process and not to the desktop — dragging a
    /// tab is not an offer to paste a GUID into whatever else is open.
    /// </summary>
    private static readonly DataFormat<string> TabFormat =
        DataFormat.CreateStringApplicationFormat("winmux-tab");

    /// <summary>
    /// Let a tab be dropped on this strip, whichever strip it came from.
    ///
    /// <para>
    /// The index is worked out from the tab rectangles rather than from the distance dragged,
    /// because tabs are not all the same width — a title of "build" and one of "npm run watch"
    /// differ by a factor of three, and a distance-based guess lands on the wrong one constantly.
    /// Past the last tab means the end, which is what dropping on the empty part of a strip means.
    /// </para>
    /// </summary>
    private static void AcceptDroppedTabs(
        Border band,
        Panel items,
        Border caret,
        IReadOnlyList<Control> tabs,
        LayoutTabStrip strip,
        bool vertical,
        TabStripCommands commands)
    {
        DragDrop.SetAllowDrop(band, true);

        var resting = band.BorderBrush;

        static bool CarriesTab(DragEventArgs e) => e.DataTransfer.Contains(TabFormat);

        int IndexFor(DragEventArgs e) =>
            TabDropIndex.For([.. tabs.Select(tab => tab.Bounds)], e.GetPosition(items), vertical);

        void Preview(int index)
        {
            if (TabDropIndex.CaretFor([.. tabs.Select(tab => tab.Bounds)], index, vertical) is not { } at)
            {
                caret.IsVisible = false;
                return;
            }

            caret.Margin = new Thickness(at.X, at.Y, 0, 0);
            caret.Width = at.Width;
            caret.Height = at.Height;
            caret.IsVisible = true;

            // The band says which group is about to receive it, which the caret alone does not:
            // with four strips on screen, a two-pixel line is easy to miss.
            band.BorderBrush = Palette.AccentBrush;
        }

        void Clear()
        {
            caret.IsVisible = false;
            band.BorderBrush = resting;
        }

        band.AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) =>
        {
            if (!CarriesTab(e))
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            Preview(IndexFor(e));
            e.Handled = true;
        });

        // Both, because a drag can end by leaving as well as by dropping, and a caret left behind
        // on a strip nothing was dropped on is a lie about where the tab went.
        band.AddHandler(DragDrop.DragLeaveEvent, (object? _, DragEventArgs e) => Clear());

        band.AddHandler(DragDrop.DropEvent, (object? _, DragEventArgs e) =>
        {
            Clear();
            if (!CarriesTab(e)) return;
            if (e.DataTransfer.TryGetValue(TabFormat) is not { } text || !Guid.TryParse(text, out var id)) return;

            commands.MoveTabToGroup(new PaneId(id), strip.Stack, IndexFor(e));
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        });
    }

    /// <summary>
    /// Let a tab be dragged, to anywhere that takes tabs.
    ///
    /// <para>
    /// A real drag session rather than pointer capture, because the destination is usually a
    /// different control: reordering inside one strip and moving a tab to the group in the next
    /// split are the same gesture, and only one of them ends where it began. The strip that
    /// receives it does the arithmetic — see <see cref="AcceptDroppedTabs"/>.
    /// </para>
    ///
    /// <para>
    /// The threshold matters. Without it every click on a tab is a one-pixel drag, and selecting a
    /// tab would sometimes reorder the strip instead.
    /// </para>
    /// </summary>
    private static void EnableDragAndDrop(
        Panel items,
        IReadOnlyList<Control> tabs,
        LayoutTabStrip strip,
        bool vertical,
        TabStripCommands commands)
    {
        const double Threshold = 6;

        var pressed = -1;
        var origin = default(Point);

        // The press itself is kept, because starting a drag needs the event that began it and a
        // drag must not begin until the pointer has travelled far enough to mean one.
        PointerPressedEventArgs? began = null;

        items.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            origin = e.GetPosition(items);
            pressed = IndexAt(tabs, origin, vertical);
            began = e;
        }, RoutingStrategies.Tunnel);

        items.AddHandler(InputElement.PointerMovedEvent, (object? _, PointerEventArgs e) =>
        {
            if (pressed < 0 || pressed >= strip.Stack.Children.Count || began is null) return;
            if (!e.GetCurrentPoint(items).Properties.IsLeftButtonPressed) { pressed = -1; return; }

            var position = e.GetPosition(items);
            var travelled = vertical ? Math.Abs(position.Y - origin.Y) : Math.Abs(position.X - origin.X);
            if (travelled < Threshold) return;

            var pane = strip.Stack.Children[pressed].Leaves().First().Pane.Id;
            var start = began;

            // The tab being carried fades where it sits, so the strip shows the gesture from both
            // ends: this one is being taken, and the caret says where it is going.
            var lifted = pressed < tabs.Count ? tabs[pressed] : null;
            if (lifted is not null) lifted.Opacity = 0.4;

            pressed = -1;
            began = null;

            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(TabFormat, pane.Value.ToString("D")));

            // Not awaited: the drag runs until the drop, and by the time it finishes this strip has
            // very likely been rebuilt by the relayout the drop caused. Nothing here survives to
            // look at the result, and the drop handler is what acts on it.
            //
            // Observed, though. A fire-and-forget task that throws surfaces as an unobserved task
            // exception, which the crash log records without any idea where it came from; a drag
            // that cannot start should say so in its own words and leave the strip working.
            DragDrop.DoDragDropAsync(start, data, DragDropEffects.Move)
                .ContinueWith(
                    task =>
                    {
                        // A drop rebuilds the strip and this control with it, but a drag abandoned
                        // with Escape or let go over nothing does not — and a tab left faded would
                        // look broken for as long as the strip lived.
                        if (lifted is not null) lifted.Opacity = 1;
                        if (task.IsFaulted) CrashLog.Write("a tab drag could not start", task.Exception);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.FromCurrentSynchronizationContext());
        }, RoutingStrategies.Tunnel);

        items.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            pressed = -1;
            began = null;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Which tab a point is over, or -1 past the last one.</summary>
    private static int IndexAt(IReadOnlyList<Control> tabs, Point point, bool vertical)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            var bounds = tabs[i].Bounds;
            var within = vertical
                ? point.Y >= bounds.Y && point.Y < bounds.Bottom
                : point.X >= bounds.X && point.X < bounds.Right;
            if (within) return i;
        }

        return -1;
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
        TabStripCommands commands,
        string? waiting)
    {
        var leaves = child.Leaves().ToArray();

        // Two different questions, and they used to share one answer. `target` is what this tab
        // *is* — pinning and closing must mean the same pane whichever inner tab happens to be
        // showing — while selecting the tab should land on whatever it was last showing.
        var target = leaves[0].Pane.Id;
        var pinned = leaves[0].Pane.IsPinned;

        // A group the user named is called that. Otherwise a tab holding a whole split describes
        // itself by what is in it, which beats showing the first title and quietly implying the
        // other panes are not there.
        //
        // Before groups could be named, the derived form was the only form -- so renaming a tab
        // that held a group renamed its first *pane*, and the group could not be called anything
        // but whatever that pane happened to be called.
        var title = child.Title.Length > 0
            ? child.Title
            : leaves.Length == 1
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

        // A pinned tab shows a pin where its close button was. Leaving a close button that refuses
        // would be worse than removing it — the control would still say the tab can be closed, and
        // the refusal would read as a bug. Clicking the pin unpins, so the way back out is where the
        // way in was.
        var corner = new Button
        {
            Content = pinned ? Icons.Pin(10) : Icons.Close(10),
            Margin = new Thickness(6, 0, -2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = pinned ? "Pinned. Click to unpin." : "Close this tab",
        };
        corner.Classes.Add(pinned ? Theme.PinButton : Theme.CloseButton);
        corner.Click += (_, e) =>
        {
            e.Handled = true;
            if (pinned) commands.TogglePin(target);
            else commands.CloseTab(target);
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(corner, Dock.Right);
        row.Children.Add(corner);

        // A dot rather than a bell: OSC 9 is a message as often as it is a ring, and an unread dot
        // is the one mark nobody has to learn. Before the title, where a list is scanned, and in
        // the accent so it reads at a glance on a tab that is not the active one.
        if (waiting is not null)
        {
            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = Palette.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                [ToolTip.TipProperty] = waiting,
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
        }

        row.Children.Add(label);

        var tab = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            [ToolTip.TipProperty] = waiting is null ? title : $"{title}{Environment.NewLine}{waiting}",
        };
        tab.Classes.Add(Theme.Tab);
        if (isActive) tab.Classes.Add(Theme.ActiveTab);
        tab.Click += (_, _) => commands.Activate(child.ActiveLeaf().Pane.Id);

        // Double-click to rename is the convention every tabbed application uses, and it costs
        // nothing: the first click of the pair has already activated the tab.
        tab.DoubleTapped += (_, e) => { e.Handled = true; commands.Rename(child); };

        tab.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item("Rename\u2026", () => commands.Rename(child)),
                Item(pinned ? "Unpin tab" : "Pin tab", () => commands.TogglePin(target)),
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
            [ToolTip.TipProperty] = "More tab actions",
        };
        button.Classes.Add(Theme.IconButton);

        // A kebab means "more actions", so it holds them rather than only the strip's placement.
        var menu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Item("Move this tab earlier", () => commands.MoveTab(-1)),
                Item("Move this tab later", () => commands.MoveTab(1)),
                new Separator(),
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
