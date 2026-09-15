using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using WinMux.Core.Layout;
using WinMux.Core.Settings;
using WinMux.Shell.Actions;

namespace WinMux.Shell.Chrome;

/// <summary>
/// The window's toolbar: every layout operation as a button, next to the keys that also do it.
///
/// WinMux is a multiplexer, so the tmux-style prefix stays the fast path — but a prefix is
/// something you have to be told about, and an application whose entire interface is undiscoverable
/// keystrokes is a terminal program wearing a window. Each button dispatches the *same named
/// action* as the keymap, the palette and the CLI (CLAUDE.md section 6), so there is one
/// implementation of "split into columns" and four ways to ask for it.
///
/// It lives in its own dock region, never over the canvas: a pane hosting a native window paints
/// above anything the shell draws, and a toolbar drawn over a pane would disappear behind Explorer.
/// </summary>
internal static class ShellToolbar
{
    /// <param name="dispatch">Runs a named action, exactly as a key binding would.</param>
    /// <param name="setTabPlacement">Moves the focused stack's tabs. Not a named action: it needs a target.</param>
    /// <param name="openProfile">Opens one profile in a pane. Split when the second argument is true.</param>
    /// <param name="profiles">The live profile list, read each time a menu opens so it never goes stale.</param>
    public static Control Build(
        Action<string> dispatch,
        Action<TabStripPlacement> setTabPlacement,
        Action<LaunchProfile, bool> openProfile,
        Func<IReadOnlyList<LaunchProfile>> profiles)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The New menu IS the profile list. Adding a profile adds it here, in the palette and in
        // an empty pane's launcher at once, because all three read the same list.
        bar.Children.Add(ProfileButton(dispatch, openProfile, profiles));

        bar.Children.Add(Command(Icons.Folder(), "Files", "Open the file browser in a new tab",
            ShellActionNames.NewFileBrowser, dispatch));

        bar.Children.Add(Divider());

        bar.Children.Add(Command(Icons.SplitColumns(), "Split right", "Split the focused pane into columns",
            ShellActionNames.SplitColumns, dispatch));
        bar.Children.Add(Command(Icons.SplitRows(), "Split down", "Split the focused pane into rows",
            ShellActionNames.SplitRows, dispatch));

        bar.Children.Add(Divider());

        bar.Children.Add(SplitButton(
            Icons.TabGroup(), "Tab group", "Turn the focused pane into a tab group",
            () => dispatch(ShellActionNames.NewTab),
            [
                ("New tab here", ShellActionNames.NewTab),
                ("New tab, tabs down the side", ShellActionNames.NewTabVertical),
            ],
            dispatch,
            extras:
            [
                ("Move tabs to the top", () => setTabPlacement(TabStripPlacement.Top)),
                ("Move tabs to the bottom", () => setTabPlacement(TabStripPlacement.Bottom)),
                ("Move tabs to the left", () => setTabPlacement(TabStripPlacement.Left)),
                ("Move tabs to the right", () => setTabPlacement(TabStripPlacement.Right)),
            ]));

        bar.Children.Add(Divider());
        bar.Children.Add(Command(Icons.Close(16), "Close pane", "Close the focused pane",
            ShellActionNames.ClosePane, dispatch));

        bar.Children.Add(Divider());

        // Session and settings live in the same row as everything else.
        //
        // They were a separate right-aligned group once, and did not render — not the buttons, not
        // a debug background on the panel itself — while layout reported the group as visible with
        // sensible bounds. That was recorded as an unexplained Avalonia failure. It is now very
        // likely to have been neither Avalonia nor right-alignment: the caption buttons produced
        // the identical symptom later, and the cause was a DPI-unaware screenshot capturing the
        // wrong region of the screen (ADR 0016). Nobody has re-tested the right-aligned group, so
        // these stay in the flow — which is where Windows Terminal keeps its equivalents anyway.
        // The row scrolls when the window is too narrow, so a button can be off-screen but never
        // silently absent.
        bar.Children.Add(SplitButton(
            Icons.Save(), string.Empty, "Save the session now",
            () => dispatch(ShellActionNames.SaveSession),
            [
                ("Open session…", ShellActionNames.OpenSession),
                ("Save", ShellActionNames.SaveSession),
                ("Save as…", ShellActionNames.SaveSessionAs),
            ],
            dispatch));
        bar.Children.Add(IconOnly(Icons.Settings(), "Settings", ShellActionNames.ShowSettings, dispatch));
        bar.Children.Add(IconOnly(Icons.Commands(), "All commands by name (Ctrl+B then :)",
            ShellActionNames.ShowPalette, dispatch));

        var layout = new ScrollViewer
        {
            Content = bar,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        return new Border
        {
            Background = Palette.RaisedBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = layout,
        };
    }

    /// <summary>"New", with every profile behind it and a way to add one.</summary>
    private static Control ProfileButton(
        Action<string> dispatch,
        Action<LaunchProfile, bool> openProfile,
        Func<IReadOnlyList<LaunchProfile>> profiles)
    {
        var main = Styled(WithLabel(Icons.Terminal(), "New"), "Open the default terminal in a new tab");
        main.CornerRadius = new CornerRadius(4, 0, 0, 4);
        main.Click += (_, _) => dispatch(ShellActionNames.NewTab);

        var chevron = Styled(Icons.Chevron(), "Everything you can open");
        chevron.Classes.Remove(Theme.ToolbarButton);
        chevron.Classes.Add(Theme.IconButton);
        chevron.CornerRadius = new CornerRadius(0, 4, 4, 0);
        chevron.Padding = new Thickness(3, 6);

        var menu = new ContextMenu();
        chevron.Click += (_, _) =>
        {
            // Rebuilt on every open rather than cached: a profile added in Settings has to appear
            // without restarting, and this menu is the most likely place someone looks for it.
            var items = new List<Control>();
            var live = profiles();

            foreach (var profile in live.Where(p => p.Kind == ProfileKind.Terminal))
                items.Add(ProfileItem(profile, openProfile));

            var apps = live.Where(p => p.Kind == ProfileKind.Application).ToArray();
            if (apps.Length > 0)
            {
                items.Add(new Separator());
                foreach (var profile in apps) items.Add(ProfileItem(profile, openProfile));
            }

            items.Add(new Separator());
            var empty = new MenuItem { Header = "Empty pane" };
            empty.Click += (_, _) => dispatch(ShellActionNames.NewEmptyPane);
            items.Add(empty);

            var manage = new MenuItem { Header = "Add or edit profiles…" };
            manage.Click += (_, _) => dispatch(ShellActionNames.ShowSettings);
            items.Add(manage);

            menu.ItemsSource = items;
            menu.PlacementTarget = chevron;
            menu.Open(chevron);
        };

        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        group.Children.Add(main);
        group.Children.Add(chevron);
        return group;
    }

    private static MenuItem ProfileItem(LaunchProfile profile, Action<LaunchProfile, bool> openProfile)
    {
        var item = new MenuItem { Header = profile.Name };
        item.Click += (_, _) => openProfile(profile, false);

        var split = new MenuItem { Header = "Split right with this" };
        split.Click += (_, _) => openProfile(profile, true);
        item.ItemsSource = new[]
        {
            Leaf("Open in a new tab", () => openProfile(profile, false)),
            split,
        };
        return item;
    }

    private static MenuItem Leaf(string header, Action invoke)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => invoke();
        return item;
    }

    private static Control Command(Control icon, string label, string tip, string action, Action<string> dispatch)
    {
        var button = Styled(WithLabel(icon, label), tip);
        button.Click += (_, _) => dispatch(action);
        return button;
    }

    private static Control IconOnly(Control icon, string tip, string action, Action<string> dispatch)
    {
        var button = Styled(icon, tip);
        button.Classes.Remove(Theme.ToolbarButton);
        button.Classes.Add(Theme.IconButton);
        button.Click += (_, _) => dispatch(action);
        return button;
    }

    /// <summary>A primary action with a dropdown of variants attached to its right edge.</summary>
    private static Control SplitButton(
        Control icon,
        string label,
        string tip,
        Action primary,
        (string Header, string Action)[] variants,
        Action<string> dispatch,
        (string Header, Action Invoke)[]? extras = null)
    {
        var main = Styled(WithLabel(icon, label), tip);
        main.CornerRadius = new CornerRadius(4, 0, 0, 4);

        var chevron = Styled(Icons.Chevron(), "More");
        chevron.Classes.Remove(Theme.ToolbarButton);
        chevron.Classes.Add(Theme.IconButton);
        chevron.CornerRadius = new CornerRadius(0, 4, 4, 0);
        chevron.Padding = new Thickness(3, 6);

        main.Click += (_, _) => primary();

        var items = new List<Control>();
        foreach (var (header, action) in variants)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => dispatch(action);
            items.Add(item);
        }
        if (extras is { Length: > 0 })
        {
            items.Add(new Separator());
            foreach (var (header, invoke) in extras)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => invoke();
                items.Add(item);
            }
        }

        var menu = new ContextMenu { ItemsSource = items };
        chevron.Click += (_, _) => { menu.PlacementTarget = chevron; menu.Open(chevron); };
        chevron.ContextMenu = menu;

        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        group.Children.Add(main);
        group.Children.Add(chevron);
        return group;
    }

    private static Control WithLabel(Control icon, string label)
    {
        if (string.IsNullOrEmpty(label)) return icon;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Button Styled(object content, string tip)
    {
        var button = new Button
        {
            Content = content,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = tip,
        };
        button.Classes.Add(Theme.ToolbarButton);
        return button;
    }

    private static Control Divider() => new Border
    {
        Width = 1,
        Margin = new Thickness(6, 8),
        Background = Palette.EdgeBrush,
    };
}
