using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>A long piece of read-only text — a licence — in a window that scrolls.</summary>
internal sealed class TextFileWindow : Window
{
    public TextFileWindow(string title, string text)
    {
        Title = title;
        Width = 720;
        Height = 640;
        MinWidth = 420;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        AppIcon.Apply(this);

        // A TextBox rather than a TextBlock: it selects, copies and scrolls on its own, and a
        // forty-component notices file is something people search through.
        Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = Palette.CaptionSize,
            Margin = new Thickness(Palette.GapMedium),
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }
}
