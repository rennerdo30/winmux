using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Shell.Actions;

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
    Func<PaneId, Task> CloseTab,
    Action<PaneId> TogglePin,
    Action<PaneId, StackNode, int> MoveTabToGroup,
    Action<StackNode> AddTab,
    Action<StackNode, TabStripPlacement> MoveStrip,
    Action<int> MoveTab,
    Action<StackNode, int>? ResizeStrip = null,
    Action<string>? Dispatch = null,
    Action<LaunchProfile, bool>? OpenProfile = null,
    Func<IReadOnlyList<LaunchProfile>>? Profiles = null);

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
    internal const double CompactTabHeight = 30;
    internal const double CompactTabMinWidth = 80;
    internal const double CompactTabMaxWidth = 220;
    internal const double CompactTabTitleSize = 11;
    private const double AccentWeight = 2;
    private sealed record RetainedStrip(StackNode Stack, TabStripPlacement Placement,
        LayoutNode[] Children, PaneId[] Leaves, bool[] Pinned, string?[] Waiting, Action<PaneId>[] Refresh);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, RetainedStrip> Retained = new();
    private sealed class SeenChildren { public HashSet<LayoutNode> Nodes { get; } = []; }
    private static readonly ConditionalWeakTable<StackNode, SeenChildren> Seen = new();

    /// <summary>Refresh labels and selection without replacing buttons or their open menus.</summary>
    public static bool TryRefresh(Control view, LayoutTabStrip strip, PaneId focused,
        Func<PaneId, string?>? waiting = null)
    {
        if (!Retained.TryGetValue(view, out var state) || state.Stack != strip.Stack ||
            state.Placement != strip.Placement || !state.Children.SequenceEqual(strip.Stack.Children) ||
            !state.Leaves.SequenceEqual(strip.Stack.Leaves().Select(l => l.Pane.Id)) ||
            !state.Pinned.SequenceEqual(strip.Stack.Children.Select(c => c.Leaves().First().Pane.IsPinned)) ||
            !state.Waiting.SequenceEqual(strip.Stack.Children.Select(c => WaitingFor(c, waiting)))) return false;
        foreach (var refresh in state.Refresh) refresh(focused);
        return true;
    }

    private static string? WaitingFor(LayoutNode child, Func<PaneId, string?>? waiting) =>
        waiting is null ? null : child.Leaves().Select(l => waiting(l.Pane.Id)).FirstOrDefault(m => m is not null);

    /// <param name="waiting">
    /// What a pane is waiting to tell the user, or null. A tab whose pane is waiting says so, which
    /// is how "one of these six panes rang" becomes "that one".
    /// </param>
    public static Control Build(
        LayoutTabStrip strip,
        PaneId focused,
        TabStripCommands commands,
        Func<PaneId, string?>? waiting = null,
        bool? closeMotionOverride = null,
        bool isCaption = false)
    {
        var vertical = strip.IsVertical;
        var items = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = vertical ? 2 : 3,
            Margin = vertical ? new Thickness(0) : new Thickness(0, 4, 0, 0),
        };

        // Does focus live anywhere inside this stack? With several strips on screen, "which tabs do
        // my keys act on" stops being obvious, so the focused one is marked.
        var stackHasFocus = strip.Stack.Leaves().Any(leaf => leaf.Pane.Id == focused);

        var tabs = new List<Control>();
        var separators = new List<Border>();
        var refreshers = new List<Action<PaneId>>();
        var seenBefore = Seen.TryGetValue(strip.Stack, out var seen);
        seen ??= Seen.GetOrCreateValue(strip.Stack);
        for (var index = 0; index < strip.Stack.Children.Count; index++)
        {
            if (!vertical && index > 0)
            {
                var separator = new Border
                {
                    Width = 1,
                    Height = 16,
                    Margin = new Thickness(1, 0),
                    Background = Palette.EdgeBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                };
                separators.Add(separator);
                items.Children.Add(separator);
            }
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
                asking,
                refreshers,
                opening: seenBefore && !seen.Nodes.Contains(child),
                closeMotion: closeMotionOverride ?? CaptionMotion.Enabled);

            tabs.Add(tab);
            items.Children.Add(tab);
        }
        seen.Nodes.Clear();
        foreach (var child in strip.Stack.Children) seen.Nodes.Add(child);
        void UpdateSeparators()
        {
            for (var i = 1; i < tabs.Count; i++)
                separators[i - 1].Opacity = i == strip.Stack.ActiveIndex || i - 1 == strip.Stack.ActiveIndex ||
                    tabs[i].IsPointerOver || tabs[i - 1].IsPointerOver ? 0 : 1;
        }
        if (!vertical)
        {
            foreach (var tab in tabs)
            {
                tab.PointerEntered += (_, _) => UpdateSeparators();
                tab.PointerExited += (_, _) => UpdateSeparators();
            }
            refreshers.Add(_ => UpdateSeparators());
            UpdateSeparators();
        }

        EnableDragAndDrop(items, tabs, strip, vertical, commands);

        var add = Icon(Icons.Add(), "Add a tab to this group", () => commands.AddTab(strip.Stack), false);
        var more = PlacementButton(strip, commands, vertical);
        if (vertical)
        {
            items.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(0, 4, 0, 0),
                Children = { add, more },
            });
        }
        else
        {
            SizeCaptionActions(add, more);
            items.Children.Add(add);
            items.Children.Add(more);
        }

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

        // One indicator moves between tabs instead of abruptly swapping a mark on each button.
        // It shares the scroller with the buttons, so overflow cannot detach it from its tab.
        var indicator = new Border
        {
            Width = 16,
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = Palette.AccentBrush,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0,
            Transitions = CaptionMotion.IndicatorTransitions(),
        };
        var indicatorPlaced = false;
        var focusedPane = focused;
        void UpdateIndicator()
        {
            if (vertical) return;
            var active = strip.Stack.ActiveIndex;
            var focused = strip.Stack.Leaves().Any(l => l.Pane.Id == focusedPane);
            if (!focused || active < 0 || active >= tabs.Count || tabs[active].Bounds.Width <= 0)
            {
                indicator.Opacity = 0;
                return;
            }
            var tab = tabs[active];
            var left = tab.Bounds.X + (tab.Bounds.Width - indicator.Width) / 2;
            if (!indicatorPlaced)
            {
                indicator.Transitions = null;
                indicator.Margin = new Thickness(left, 0, 0, 0);
                indicator.Transitions = CaptionMotion.IndicatorTransitions();
                indicatorPlaced = true;
            }
            else indicator.Margin = new Thickness(left, 0, 0, 0);
            indicator.Opacity = 1;
        }
        items.SizeChanged += (_, _) => UpdateIndicator();
        refreshers.Add(current =>
        {
            focusedPane = current;
            Dispatcher.UIThread.Post(UpdateIndicator);
        });

        var scroller = new ScrollViewer
        {
            Content = new Panel { Children = { items, indicator, caret } },
            HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Padding = vertical ? new Thickness(6, 6) : new Thickness(6, 0, 0, 0),
        };
        DockPanel? row = null;
        if (!vertical)
        {
            double[]? naturalWidths = null;
            double decorationWidth = 0;
            void FitTabs()
            {
                if (row is null || row.Bounds.Width <= 0 || tabs.Count == 0) return;
                if (naturalWidths is null)
                {
                    if (tabs.Any(tab => tab.DesiredSize.Width <= 0) || items.DesiredSize.Width <= 0) return;
                    naturalWidths = [.. tabs.Select(tab => tab.DesiredSize.Width)];
                    // Reserve for scroller padding and DIP rounding so a fitting strip stays fitting.
                    decorationWidth = Math.Max(0, items.DesiredSize.Width - naturalWidths.Sum()) +
                        scroller.Padding.Left + 8;
                }
                var available = row.Bounds.Width;
                var naturalTotal = naturalWidths.Sum() + decorationWidth;
                var width = naturalTotal <= available
                    ? double.NaN
                    : Math.Clamp((available - decorationWidth) / tabs.Count,
                        CompactTabMinWidth, CompactTabMaxWidth);
                foreach (var tab in tabs)
                    if (double.IsNaN(width) ? !double.IsNaN(tab.Width) :
                            double.IsNaN(tab.Width) || Math.Abs(tab.Width - width) > 0.5)
                        tab.Width = width;
            }
            scroller.SizeChanged += (_, _) => FitTabs();
            items.SizeChanged += (_, _) => FitTabs();
            scroller.PointerWheelChanged += (_, e) =>
            {
                var maximum = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
                if (maximum <= 0) return;
                scroller.Offset = new Vector(Math.Clamp(scroller.Offset.X - e.Delta.Y * 80, 0, maximum), 0);
                e.Handled = true;
            };
        }

        Control contents = scroller;
        if (!vertical)
        {
            var fixedAdd = Icon(Icons.Add(), "Add a tab to this group", () => commands.AddTab(strip.Stack), false);
            var fixedMore = PlacementButton(strip, commands, false);
            SizeCaptionActions(fixedAdd, fixedMore);
            fixedAdd.IsVisible = false;
            fixedMore.IsVisible = false;
            static Border Fade(bool fromLeft) => new()
            {
                Width = 16,
                Background = Palette.TabStripBrush,
                OpacityMask = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    [
                        new GradientStop(fromLeft ? Colors.White : Colors.Transparent, 0),
                        new GradientStop(fromLeft ? Colors.Transparent : Colors.White, 1),
                    ],
                },
                HorizontalAlignment = fromLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                IsHitTestVisible = false,
                IsVisible = false,
            };
            var leftFade = Fade(true);
            var rightFade = Fade(false);
            var previous = Icon(new TextBlock { Text = "‹", FontSize = 20 }, "Scroll tabs left", () =>
                scroller.Offset = new Vector(Math.Max(0, scroller.Offset.X - 160), 0), false);
            var next = Icon(new TextBlock { Text = "›", FontSize = 20 }, "Scroll tabs right", () =>
                scroller.Offset = new Vector(Math.Min(scroller.Extent.Width - scroller.Viewport.Width,
                    scroller.Offset.X + 160), 0), false);
            foreach (var arrow in new[] { previous, next })
                if (arrow is Button button)
                {
                    button.Width = 32;
                    button.Height = 24;
                    button.MinHeight = 0;
                    button.Padding = new Thickness(0);
                }
            previous.IsVisible = false;
            next.IsVisible = false;
            void UpdateScrollButtons()
            {
                var overflow = scroller.Extent.Width > (row?.Bounds.Width ?? 0) + 1;
                previous.IsVisible = overflow && scroller.Offset.X > 1;
                next.IsVisible = overflow && scroller.Offset.X + scroller.Viewport.Width < scroller.Extent.Width - 1;
                leftFade.IsVisible = previous.IsVisible;
                rightFade.IsVisible = next.IsVisible;
                fixedAdd.IsVisible = overflow;
                fixedMore.IsVisible = overflow;
                add.Opacity = overflow ? 0 : 1;
                more.Opacity = overflow ? 0 : 1;
                add.IsHitTestVisible = !overflow;
                more.IsHitTestVisible = !overflow;
            }
            scroller.ScrollChanged += (_, _) => UpdateScrollButtons();
            scroller.SizeChanged += (_, _) => UpdateScrollButtons();
            row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(fixedMore, Dock.Right);
            DockPanel.SetDock(fixedAdd, Dock.Right);
            DockPanel.SetDock(next, Dock.Right);
            DockPanel.SetDock(previous, Dock.Left);
            row.Children.Add(fixedMore);
            row.Children.Add(fixedAdd);
            row.Children.Add(next);
            row.Children.Add(previous);
            row.Children.Add(new Panel { Children = { scroller, leftFade, rightFade } });
            contents = row;
        }

        var band = new Border
        {
            Background = Palette.TabStripBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = isCaption ? default : ContentEdge(strip.Placement, stackHasFocus),
            Child = vertical && commands.ResizeStrip is not null
                ? new Panel { Children = { scroller, BuildResizeHandle(strip, commands.ResizeStrip) } }
                : contents,
        };

        // The whole band accepts a drop, not only the tabs in it: a group of one tab has almost no
        // tab to aim at, and dropping into an empty part of the strip is what people try first.
        AcceptDroppedTabs(band, items, caret, tabs, strip, vertical, commands);
        Retained.Add(band, new RetainedStrip(strip.Stack, strip.Placement,
            strip.Stack.Children.ToArray(), strip.Stack.Leaves().Select(l => l.Pane.Id).ToArray(),
            strip.Stack.Children.Select(c => c.Leaves().First().Pane.IsPinned).ToArray(),
            strip.Stack.Children.Select(c => WaitingFor(c, waiting)).ToArray(), refreshers.ToArray()));
        return band;
    }

    private static void SizeCaptionActions(Control add, Control more)
    {
        if (add is Button addButton)
        {
            addButton.Width = 32;
            addButton.Height = 28;
            addButton.MinHeight = 0;
            addButton.Padding = new Thickness(0);
            addButton.CornerRadius = new CornerRadius(4, 0, 0, 4);
            addButton.Margin = new Thickness(4, 3, 0, 0);
        }
        if (more is Button moreButton)
        {
            moreButton.Width = 20;
            moreButton.Height = 28;
            moreButton.MinHeight = 0;
            moreButton.Padding = new Thickness(0);
            moreButton.CornerRadius = new CornerRadius(0, 4, 4, 0);
            moreButton.Margin = new Thickness(0, 3, 0, 0);
        }
    }

    internal static int WidthAfterDrag(int originalWidth, double horizontalDelta, TabStripPlacement placement) =>
        Math.Clamp((int)Math.Round(originalWidth +
            (placement == TabStripPlacement.Right ? -horizontalDelta : horizontalDelta)),
            StackNode.MinimumTabStripWidth, StackNode.MaximumTabStripWidth);

    private static Control BuildResizeHandle(LayoutTabStrip strip, Action<StackNode, int> resize)
    {
        var handle = new Border
        {
            Width = 6,
            Background = Brushes.Transparent,
            HorizontalAlignment = strip.Placement == TabStripPlacement.Left
                ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            [ToolTip.TipProperty] = "Drag to resize this tab strip",
        };
        Point? origin = null;
        TopLevel? surface = null;
        var originalWidth = 0;
        handle.PointerEntered += (_, _) => handle.Background = Palette.EdgeBrush;
        handle.PointerExited += (_, _) => { if (origin is null) handle.Background = Brushes.Transparent; };
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            surface = TopLevel.GetTopLevel(handle);
            if (surface is null) return;
            origin = e.GetPosition(surface);
            originalWidth = strip.Stack.TabStripWidth ?? strip.Rect.Width;
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (origin is not { } start || surface is null) return;
            resize(strip.Stack, WidthAfterDrag(originalWidth, e.GetPosition(surface).X - start.X, strip.Placement));
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (origin is null) return;
            origin = null;
            e.Pointer.Capture(null);
            handle.Background = Brushes.Transparent;
            e.Handled = true;
        };
        handle.PointerCaptureLost += (_, _) =>
        {
            origin = null;
            handle.Background = Brushes.Transparent;
        };
        return handle;
    }

    /// <summary>
    /// The format the dragged tab travels in: a pane id in its string form.
    ///
    /// An application format, so it is offered to this process and not to the desktop — dragging a
    /// tab is not an offer to paste a GUID into whatever else is open.
    /// </summary>
    private static readonly DataFormat<string> TabFormat =
        DataFormat.CreateStringApplicationFormat("winmux-tab");
    private static readonly DataFormat<string> TabTitleFormat =
        DataFormat.CreateStringApplicationFormat("winmux-tab-title");

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

        var previewTitle = new TextBlock
        {
            Foreground = Palette.TextBrush,
            FontSize = vertical ? Palette.CaptionSize : CompactTabTitleSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 170,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var preview = new Border
        {
            Child = previewTitle,
            Background = Palette.OpaqueWindowBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 5),
            Height = vertical ? Palette.ControlHeight : CompactTabHeight,
            MinWidth = CompactTabMinWidth,
            MaxWidth = CompactTabMaxWidth,
            Opacity = 0.9,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetY = 4,
                Blur = 12,
                Color = Color.FromArgb(0x66, 0, 0, 0),
            }),
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var oldContents = band.Child!;
        band.Child = null;
        band.Child = new Panel { Children = { oldContents, preview } };

        var resting = band.BorderBrush;
        Avalonia.Rect[]? restingTabs = null;
        if (CaptionMotion.Enabled)
        {
            foreach (var tab in tabs)
            {
                var transitions = tab.Transitions ?? [];
                transitions.Add(new ThicknessTransition
                {
                    Property = Layoutable.MarginProperty,
                    Duration = TimeSpan.FromMilliseconds(150),
                });
                tab.Transitions = transitions;
            }
        }

        static bool CarriesTab(DragEventArgs e) => e.DataTransfer.Contains(TabFormat);

        int IndexFor(DragEventArgs e) =>
            TabDropIndex.For(restingTabs ?? [.. tabs.Select(tab => tab.Bounds)], e.GetPosition(items), vertical);

        void Preview(int index, DragEventArgs e)
        {
            restingTabs ??= [.. tabs.Select(tab => tab.Bounds)];
            if (CaptionMotion.Enabled)
                for (var i = 0; i < tabs.Count; i++)
                    tabs[i].Margin = i == index
                        ? vertical ? new Thickness(0, 20, 0, 0) : new Thickness(20, 0, 0, 0)
                        : new Thickness(0);
            previewTitle.Text = e.DataTransfer.TryGetValue(TabTitleFormat) ?? "Moving tab";
            var pointer = e.GetPosition(band);
            preview.Margin = new Thickness(
                Math.Clamp(pointer.X + 12, 0, Math.Max(0, band.Bounds.Width - 96)),
                Math.Clamp(pointer.Y + 8, 0, Math.Max(0, band.Bounds.Height - preview.Height)), 0, 0);
            preview.IsVisible = true;
            if (TabDropIndex.CaretFor(restingTabs, index, vertical) is not { } at)
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
            preview.IsVisible = false;
            restingTabs = null;
            foreach (var tab in tabs) tab.Margin = new Thickness(0);
        }

        band.AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) =>
        {
            if (!CarriesTab(e))
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            Preview(IndexFor(e), e);
            e.Handled = true;
        });

        // Both, because a drag can end by leaving as well as by dropping, and a caret left behind
        // on a strip nothing was dropped on is a lie about where the tab went.
        band.AddHandler(DragDrop.DragLeaveEvent, (object? _, DragEventArgs e) => Clear());

        band.AddHandler(DragDrop.DropEvent, (object? _, DragEventArgs e) =>
        {
            var index = IndexFor(e);
            Clear();
            if (!CarriesTab(e)) return;
            if (e.DataTransfer.TryGetValue(TabFormat) is not { } text || !Guid.TryParse(text, out var id)) return;

            commands.MoveTabToGroup(new PaneId(id), strip.Stack, index);
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
            var source = strip.Stack.Children[pressed];
            var start = began;

            // The tab being carried fades where it sits, so the strip shows the gesture from both
            // ends: this one is being taken, and the caret says where it is going.
            var lifted = pressed < tabs.Count ? tabs[pressed] : null;
            if (lifted is not null) lifted.Opacity = 0.4;

            pressed = -1;
            began = null;

            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(TabFormat, pane.Value.ToString("D")));
            var title = source.Title.Length > 0 ? source.Title : source.ActiveLeaf().Pane.Title;
            data.Add(DataTransferItem.Create(TabTitleFormat, title));

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
        string? waiting,
        List<Action<PaneId>> refreshers,
        bool opening,
        bool closeMotion)
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
            MaxWidth = vertical ? double.PositiveInfinity : 170,
            TextAlignment = TextAlignment.Left,
            FontSize = vertical ? Palette.CaptionSize : CompactTabTitleSize,
        };

        // A pinned tab shows a pin where its close button was. Leaving a close button that refuses
        // would be worse than removing it — the control would still say the tab can be closed, and
        // the refusal would read as a bug. Unpinning needs a separate menu choice: replacing this
        // pin with Close on the first click makes a second click at the same position destructive.
        var unpinMenu = pinned ? new ContextMenu
        {
            ItemsSource = new[] { Item("Unpin tab", () => commands.TogglePin(target)) },
        } : null;
        Button? tab = null;
        var closing = false;
        async void CloseWithMotion()
        {
            if (closing) return;
            if (vertical || !closeMotion || tab is null || tab.Bounds.Width < 1)
            {
                try { await commands.CloseTab(target); }
                catch (Exception ex) { CrashLog.Write("tab close failed", ex); }
                return;
            }
            closing = true;
            var oldWidth = tab.Width;
            var oldMinWidth = tab.MinWidth;
            var oldOpacity = tab.Opacity;
            var oldTransitions = tab.Transitions;
            tab.IsHitTestVisible = false;
            tab.MinWidth = 0;
            tab.Width = tab.Bounds.Width;
            tab.Transitions =
            [
                new DoubleTransition { Property = Layoutable.WidthProperty, Duration = TimeSpan.FromMilliseconds(167) },
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(167) },
            ];
            Dispatcher.UIThread.Post(() => { tab.Width = 0; tab.Opacity = 0; });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(167) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try { await commands.CloseTab(target); }
                catch (Exception ex) { CrashLog.Write("tab close failed", ex); }
                finally
                {
                    // Confirmation can be cancelled and pane shutdown can fail. The retained
                    // control then survives, so undo every animation property before it is used.
                    if (tab.GetVisualAncestors().OfType<TopLevel>().Any())
                    {
                        tab.Transitions = [];
                        tab.Width = oldWidth;
                        tab.MinWidth = oldMinWidth;
                        tab.Opacity = oldOpacity;
                        tab.IsHitTestVisible = true;
                        tab.Transitions = oldTransitions;
                        closing = false;
                    }
                }
            };
            timer.Start();
        }
        var corner = new Button
        {
            Content = pinned ? Icons.Pin(12) : Icons.Close(12),
            Margin = vertical ? new Thickness(6, 0, 0, 0) : new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 24,
            Height = 24,
            IsVisible = vertical || pinned || isActive,
            [ToolTip.TipProperty] = pinned ? "Pinned. Open the menu to unpin." : "Close this tab",
            ContextMenu = unpinMenu,
        };
        corner.Classes.Add(pinned ? Theme.PinButton : Theme.CloseButton);
        if (isActive && child.ActiveLeaf().Pane.Kind == PaneKind.Terminal)
            corner.Classes.Add(Theme.TerminalCloseButton);
        if (vertical) corner.Classes.Add(Theme.VerticalTabCorner);
        corner.Click += (_, e) =>
        {
            e.Handled = true;
            if (pinned) unpinMenu!.Open(corner);
            else CloseWithMotion();
        };

        var row = new DockPanel { LastChildFill = true };
        var cornerSlot = new Border { Width = 24, Height = 24, Child = corner };
        DockPanel.SetDock(cornerSlot, Dock.Right);
        row.Children.Add(cornerSlot);

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

        tab = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            MinWidth = vertical ? 0 : CompactTabMinWidth,
            MaxWidth = vertical ? double.PositiveInfinity : CompactTabMaxWidth,
            Height = vertical ? double.NaN : CompactTabHeight,
            [ToolTip.TipProperty] = waiting is null ? title : $"{title}{Environment.NewLine}{waiting}",
        };
        tab.Classes.Add(Theme.Tab);
        if (child.ActiveLeaf().Pane.Kind == PaneKind.Terminal) tab.Classes.Add(Theme.TerminalTab);
        if (vertical) tab.Classes.Add(Theme.VerticalTab);
        if (isActive) tab.Classes.Add(Theme.ActiveTab);
        tab.Click += (_, _) => commands.Activate(child.ActiveLeaf().Pane.Id);
        tab.PointerEntered += (_, _) => corner.IsVisible = true;
        tab.PointerExited += (_, _) => corner.IsVisible = vertical || pinned ||
            child.Parent is StackNode stack && stack.Active == child;
        tab.PointerPressed += (_, e) =>
        {
            if (!pinned && e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed)
            {
                e.Handled = true;
                CloseWithMotion();
            }
        };

        // Double-click to rename is the convention every tabbed application uses, and it costs
        // nothing: the first click of the pair has already activated the tab.
        tab.DoubleTapped += (_, e) => { e.Handled = true; commands.Rename(child); };

        var closeTab = Item("Close tab", CloseWithMotion);
        closeTab.IsEnabled = !pinned;
        tab.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item("Rename\u2026", () => commands.Rename(child)),
                Item(pinned ? "Unpin tab" : "Pin tab", () => commands.TogglePin(target)),
                closeTab,
            },
        };

        // The accent strip is added to *every* tab, transparent when inactive. Adding it only to the
        // active one made that tab two pixels shorter than its neighbours, so the row visibly
        // shifted as the selection moved.
        var accent = new Border
        {
            Background = isActive && hasFocus ? Palette.AccentBrush : Brushes.Transparent,
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false,
        };

        Control stacked;
        if (!vertical)
        {
            stacked = tab;
            if (opening && CaptionMotion.Enabled)
            {
                tab.MinWidth = 0;
                tab.MaxWidth = 0;
                tab.Opacity = 0;
                tab.Transitions =
                [
                    new DoubleTransition { Property = Layoutable.MaxWidthProperty, Duration = TimeSpan.FromMilliseconds(167) },
                    new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(167) },
                ];
                tab.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    tab.MaxWidth = CompactTabMaxWidth;
                    tab.Opacity = 1;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(167) };
                    timer.Tick += (_, _) => { timer.Stop(); tab.MinWidth = CompactTabMinWidth; };
                    timer.Start();
                });
            }
        }
        else
        {
            var column = new DockPanel { LastChildFill = true };
            accent.Width = AccentWeight;
            DockPanel.SetDock(accent, placement == TabStripPlacement.Left ? Dock.Right : Dock.Left);
            column.Children.Add(accent);
            column.Children.Add(tab);
            stacked = column;
        }
        refreshers.Add(focused =>
        {
            var active = child.Parent is StackNode stack && stack.Active == child;
            var focus = child.Leaves().Any(l => l.Pane.Id == focused);
            if (active) tab.Classes.Add(Theme.ActiveTab);
            else tab.Classes.Remove(Theme.ActiveTab);
            corner.Classes.Set(Theme.TerminalCloseButton,
                active && child.ActiveLeaf().Pane.Kind == PaneKind.Terminal);
            if (vertical) accent.Background = active && focus ? Palette.AccentBrush : Brushes.Transparent;
            corner.IsVisible = vertical || pinned || active || tab.IsPointerOver;
            var current = child.Title.Length > 0 ? child.Title : leaves.Length == 1
                ? leaves[0].Pane.Title : $"{leaves[0].Pane.Title}  +{leaves.Length - 1}";
            if (string.IsNullOrWhiteSpace(current)) current = "untitled";
            label.Text = current;
            ToolTip.SetTip(tab, waiting is null ? current : $"{current}{Environment.NewLine}{waiting}");
        });
        return stacked;
    }

    private static Control PlacementButton(LayoutTabStrip strip, TabStripCommands commands, bool vertical)
    {
        var button = new Button
        {
            Content = vertical ? Icons.More() : Icons.Chevron(),
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            [ToolTip.TipProperty] = "More tab actions",
        };
        button.Classes.Add(Theme.IconButton);

        // Build on open so profiles added in Settings appear without rebuilding the tab strip.
        List<Control> BuildMenuItems()
        {
            var items = new List<Control>();
            if (commands.Dispatch is { } dispatch)
            {
                items.Add(Item("New terminal tab", () => dispatch(ShellActionNames.NewTab)));
                items.Add(Item("File browser tab", () => dispatch(ShellActionNames.NewFileBrowser)));
                if (commands.Profiles is { } profiles && commands.OpenProfile is { } openProfile)
                {
                    var profileItems = profiles().Select(profile =>
                        Item(profile.Name, () => openProfile(profile, false))).ToArray();
                    if (profileItems.Length > 0)
                        items.Add(new MenuItem { Header = "Open profile", ItemsSource = profileItems });
                }
                items.Add(new Separator());
                items.Add(Item("Split right", () => dispatch(ShellActionNames.SplitColumns)));
                items.Add(Item("Split down", () => dispatch(ShellActionNames.SplitRows)));
                items.Add(Item("New tab group", () => dispatch(ShellActionNames.NewTabGroup)));
                items.Add(Item("Close pane", () => dispatch(ShellActionNames.ClosePane)));
                items.Add(new Separator());
                items.Add(Item("Save session", () => dispatch(ShellActionNames.SaveSession)));
                items.Add(Item("Settings", () => dispatch(ShellActionNames.ShowSettings)));
                items.Add(Item("All commands…", () => dispatch(ShellActionNames.ShowPalette)));
                items.Add(new Separator());
            }
            items.Add(Item("Move this tab earlier", () => commands.MoveTab(-1)));
            items.Add(Item("Move this tab later", () => commands.MoveTab(1)));
            items.Add(new Separator());
            items.Add(PlacementItem(strip, commands, TabStripPlacement.Top, "Tabs on top"));
            items.Add(PlacementItem(strip, commands, TabStripPlacement.Bottom, "Tabs on the bottom"));
            items.Add(PlacementItem(strip, commands, TabStripPlacement.Left, "Tabs on the left"));
            items.Add(PlacementItem(strip, commands, TabStripPlacement.Right, "Tabs on the right"));
            return items;
        }
        var menu = new ContextMenu { ItemsSource = BuildMenuItems() };
        menu.Opening += (_, _) => menu.ItemsSource = BuildMenuItems();

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
