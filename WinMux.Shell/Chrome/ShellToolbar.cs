using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using WinMux.Core.Layout;
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
    public static Control Build(Action<string> dispatch, Action<TabStripPlacement> setTabPlacement)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

        bar.Children.Add(SplitButton(
            Icons.Terminal(), "Terminal", "Open a terminal in a new tab",
            () => dispatch(ShellActionNames.NewTab),
            [
                ("Command Prompt", ShellActionNames.NewTerminalCmd),
                ("PowerShell 7", ShellActionNames.NewTerminalPowerShell),
                ("Windows PowerShell", ShellActionNames.NewTerminalWindowsPowerShell),
                ("WSL", ShellActionNames.NewTerminalWsl),
            ],
            dispatch));

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

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(IconOnly(Icons.Save(), "Save the session now", ShellActionNames.SaveSession, dispatch));
        right.Children.Add(IconOnly(Icons.Commands(), "All commands by name (Ctrl+B then :)",
            ShellActionNames.ShowPalette, dispatch));

        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(bar, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(bar);
        dock.Children.Add(right);

        return new Border
        {
            Background = Palette.RaisedBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = dock,
        };
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
