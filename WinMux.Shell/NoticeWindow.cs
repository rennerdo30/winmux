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
    public NoticeWindow(string title, string message)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 17,
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

        var close = new Button
        {
            Content = "OK",
            IsDefault = true,
            MinWidth = 96,
            Padding = new Thickness(14, 7),
            CornerRadius = Palette.ControlRadius,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();

        // Windows 11 separates a dialog's actions from its content with a shaded footer rather than
        // floating the button in the same space as the text.
        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = close,
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
