using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WinMux.Shell.Actions;

namespace WinMux.Shell;

/// <summary>
/// A top-level command palette. It deliberately is not drawn over the pane canvas: native pane
/// windows would paint over an Avalonia overlay (CLAUDE.md section 6).
/// </summary>
internal sealed class CommandPaletteWindow : Window
{
    private readonly TextBox _query = new() { PlaceholderText = "Type an action name…", Margin = new Thickness(12, 12, 12, 6) };
    private readonly ListBox _results = new() { Margin = new Thickness(12, 0, 12, 12), MaxHeight = 360 };
    private readonly string[] _actions;
    private readonly Action<string> _dispatch;

    public CommandPaletteWindow(IEnumerable<string> actions, Action<string> dispatch)
    {
        _actions = actions.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _dispatch = dispatch;

        Title = "WinMux command palette";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
        Content = new StackPanel { Children = { _query, _results } };

        _query.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Refresh();
        };
        _query.KeyDown += OnKeyDown;
        _results.KeyDown += OnKeyDown;
        _results.DoubleTapped += (_, _) => InvokeSelected();
        Opened += (_, _) => { Refresh(); _query.Focus(); };
    }

    private void Refresh()
    {
        var query = _query.Text?.Trim() ?? "";
        var matches = _actions
            .Where(action => query.Length == 0 || action.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _results.ItemsSource = matches;
        _results.SelectedIndex = matches.Length == 0 ? -1 : 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            InvokeSelected();
            e.Handled = true;
        }
        else if (sender == _query && e.Key is Key.Down or Key.Up)
        {
            var count = (_results.ItemsSource as string[])?.Length ?? 0;
            if (count > 0)
            {
                var delta = e.Key == Key.Down ? 1 : -1;
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + delta, 0, count - 1);
            }
            e.Handled = true;
        }
    }

    private void InvokeSelected()
    {
        if (_results.SelectedItem is not string action) return;
        Close();
        _dispatch(action);
    }
}
