using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// Ask for one short piece of text.
///
/// Small enough to be tempting to skip, and worth having: renaming a pane through a modal that
/// opens with the text selected and closes on Enter is two keystrokes, while renaming it through a
/// settings page would be ten.
/// </summary>
internal sealed class PromptWindow : Window
{
    private readonly TextBox _input;

    /// <summary>What the user typed, or null if they backed out.</summary>
    public string? Result { get; private set; }

    /// <summary>True when the user asked to clear the value rather than set one.</summary>
    public bool Cleared { get; private set; }

    /// <param name="title">The window caption.</param>
    /// <param name="prompt">A sentence above the box saying what the value does.</param>
    /// <param name="value">The current value, pre-selected so typing replaces it.</param>
    /// <param name="clearLabel">When given, offers a third button that clears the value.</param>
    public PromptWindow(string title, string prompt, string value, string? clearLabel = null)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        AppIcon.Apply(this);

        _input = new TextBox
        {
            Text = value,
            CornerRadius = Palette.ControlRadius,
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Accept();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = prompt,
                    Foreground = Palette.MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
                _input,
            },
        };

        var ok = Button("OK", isDefault: true);
        ok.Click += (_, _) => Accept();

        var cancel = Button("Cancel", isCancel: true);
        cancel.Click += (_, _) => Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actions.Children.Add(cancel);
        actions.Children.Add(ok);

        var footerContent = new DockPanel { LastChildFill = false };
        if (clearLabel is not null)
        {
            var clear = Button(clearLabel);
            clear.Click += (_, _) => { Cleared = true; Close(); };
            DockPanel.SetDock(clear, Dock.Left);
            footerContent.Children.Add(clear);
        }
        DockPanel.SetDock(actions, Dock.Right);
        footerContent.Children.Add(actions);

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = footerContent,
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // Selected, not just focused: the point of a rename box is that typing replaces what is
        // there, without having to clear it first.
        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    private static Button Button(string content, bool isDefault = false, bool isCancel = false) => new()
    {
        Content = content,
        IsDefault = isDefault,
        IsCancel = isCancel,
        Classes = { Chrome.Theme.DialogButton },
    };

    private void Accept()
    {
        Result = _input.Text?.Trim() ?? string.Empty;
        Close();
    }
}
