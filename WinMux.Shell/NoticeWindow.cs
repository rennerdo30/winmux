using Avalonia;
using Avalonia.Controls;

namespace WinMux.Shell;

internal sealed class NoticeWindow : Window
{
    public NoticeWindow(string title, string message)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        var close = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                close,
            },
        };
    }
}
