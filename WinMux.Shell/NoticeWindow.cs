using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// A modal WinMux has something to say in.
///
/// Worth more care than its size suggests: the restore notice is the **first thing** a user sees
/// after opening a session, before they have looked at a single pane. A bare box with a default
/// button is the whole application's first impression.
/// </summary>
internal sealed class NoticeWindow : Window
{
    /// <summary>
    /// Ask a yes/no question in the same dialog chrome. Returns false when the user cancels,
    /// closes the window, or presses Esc — every "did not say yes" is a no.
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText,
        string cancelText = "Cancel")
    {
        var dialog = new NoticeWindow(title, message, confirmText, cancelText);
        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }

    /// <summary>True only when the user pressed the affirmative button.</summary>
    public bool Confirmed { get; private set; }

    public NoticeWindow(string title, string message) : this(title, message, "OK", null)
    {
    }

    private NoticeWindow(string title, string message, string confirmText, string? cancelText)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        Chrome.AppIcon.Apply(this);

        var heading = new TextBlock
        {
            Text = title,
            FontSize = Palette.SubtitleSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TextBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.MutedTextBrush,
            LineHeight = 19,
        };

        var confirm = new Button
        {
            Content = confirmText,
            IsDefault = true,
            Classes = { Chrome.Theme.DialogButton },
        };
        confirm.Click += (_, _) => { Confirmed = true; Close(); };

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (cancelText is not null)
        {
            var cancel = new Button
            {
                Content = cancelText,
                IsCancel = true,
                Classes = { Chrome.Theme.DialogButton },
            };
            cancel.Click += (_, _) => Close();
            actions.Children.Add(cancel);
        }
        actions.Children.Add(confirm);

        // Windows 11 separates a dialog's actions from its content with a shaded footer rather than
        // floating the button in the same space as the text.
        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = actions,
        };

        var content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 20),
            Spacing = 10,
            Children = { heading, body },
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(content);
        Content = root;

        // Esc should dismiss anything that only wants an acknowledgement.
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }
}
