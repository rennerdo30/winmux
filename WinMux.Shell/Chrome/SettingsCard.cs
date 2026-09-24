using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WinMux.Shell.Chrome;

/// <summary>
/// The row Windows 11 Settings is built out of: a rounded card holding a label, an optional line of
/// explanation, and the control that changes the thing.
///
/// This is the single most recognisable element of the modern Windows look, and its absence was
/// most of why the settings dialog read as a legacy properties sheet. A label in the left column of
/// a grid with a combo box in the right is what Windows looked like in 2009; the same information
/// in a card with its own surface, border and description is what it looks like now.
///
/// WinUI ships this as <c>SettingsCard</c>. It is reproduced here rather than taken, because
/// pulling in the WinUI control library would mean giving up Avalonia, and with it the portability
/// that CLAUDE.md section 1 keeps open.
/// </summary>
internal static class SettingsCard
{
    /// <summary>Minimum card height, from WinUI. Shorter cards read as a cramped list.</summary>
    private const double MinHeight = 60;

    /// <summary>A card whose control sits on the right, opposite the label.</summary>
    public static Control Row(string label, Control control, string? description = null) =>
        Build(label, description, control, stacked: false);

    /// <summary>
    /// A card whose control sits beneath the label, for anything too wide to sit beside it —
    /// a list, or a row of buttons.
    /// </summary>
    public static Control Stacked(string label, Control control, string? description = null) =>
        Build(label, description, control, stacked: true);

    /// <summary>
    /// A card showing a fact rather than a setting.
    ///
    /// Selectable, because the reason to look at one of these is almost always to copy it. Visibly
    /// not a control, because a settings page that styles read-only information like a setting
    /// invites people to try to change it.
    /// </summary>
    public static Control Info(string label, string value) =>
        Build(label, null, Fact(value, TextTrimming.CharacterEllipsis), stacked: false);

    /// <summary>
    /// A card showing a file, with a button that opens the folder it is in.
    ///
    /// <para>
    /// The value is one line that ellipsises, never wrapped. Wrapping is what this did until
    /// 2026-09-24, and a Windows path breaks at its separators, so `…\WinMux\session.toml` came out
    /// as three right-aligned fragments with `C:` alone on the first line and `ml` alone on the
    /// third. The full path is on the tooltip, where a truncated one belongs.
    /// </para>
    /// </summary>
    public static Control File(string label, string path, Action open)
    {
        // PathSegmentEllipsis drops whole directories from the middle, so what survives is the
        // drive and the file name — the two parts of a path anyone is actually reading for.
        var value = Fact(path, TextTrimming.PathSegmentEllipsis);
        ToolTip.SetTip(value, path);

        var button = new Button
        {
            Content = "Open folder",
            MinHeight = Palette.ControlHeight,
            Padding = Palette.ButtonPadding,
            CornerRadius = Palette.ControlRadius,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Palette.GapMedium, 0, 0, 0),
        };
        button.Click += (_, _) => open();

        return Build(label, null, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { value, button },
        }, stacked: false);
    }

    /// <summary>
    /// Something the user needs to know before a setting above it can work — a permission Windows
    /// has withheld, a program that has not been told about WinMux — with the one action that fixes
    /// it.
    ///
    /// Its own card rather than another sentence in a description: a blocked feature and an
    /// explanation of a working one were the same muted grey, so the one thing on the page that
    /// needed doing was the easiest thing on it to miss.
    /// </summary>
    public static Control Warning(string text, string actionLabel, Action act)
    {
        var button = new Button
        {
            Content = actionLabel,
            MinHeight = Palette.ControlHeight,
            Padding = Palette.ButtonPadding,
            CornerRadius = Palette.ControlRadius,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Palette.GapLarge, 0, 0, 0),
        };
        button.Click += (_, _) => act();

        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
        };
        var message = new TextBlock
        {
            Text = text,
            Foreground = Palette.TextBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(message, 0);
        Grid.SetColumn(button, 1);
        grid.Children.Add(message);
        grid.Children.Add(button);

        return new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.DangerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Palette.ControlRadius,
            Padding = new Thickness(Palette.GapLarge, Palette.GapMedium),
            MinHeight = MinHeight,
            Child = grid,
        };
    }

    private static SelectableTextBlock Fact(string value, TextTrimming trimming) => new()
    {
        Text = value,
        Foreground = Palette.FaintTextBrush,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = trimming,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        TextAlignment = TextAlignment.Right,
        MaxWidth = 340,
    };

    private static Control Build(string label, string? description, Control control, bool stacked)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Palette.TextBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrEmpty(description))
        {
            text.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Palette.CaptionSize,
                LineHeight = 16,
                Foreground = Palette.MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Control inner;
        if (stacked)
        {
            control.Margin = new Thickness(0, Palette.GapMedium, 0, 0);
            inner = new StackPanel { Children = { text, control } };
        }
        else
        {
            // The label takes the slack and the control takes what it needs, so a card's control
            // ends at the same right edge as every other card's. That alignment is what makes a
            // column of cards look deliberate rather than merely rounded.
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(Palette.GapLarge, 0, 0, 0);

            var grid = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
            inner = grid;
        }

        return new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Palette.ControlRadius,
            Padding = new Thickness(Palette.GapLarge, Palette.GapMedium),
            MinHeight = MinHeight,
            Child = inner,
        };
    }

    /// <summary>
    /// A group heading. Sentence case and semibold body size, which is what Windows 11 Settings
    /// uses — the small uppercase headers this replaces are a Windows 7 control-panel idiom.
    /// </summary>
    public static Control Heading(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Foreground = Palette.TextBrush,
        Margin = new Thickness(2, Palette.GapLarge, 0, Palette.GapSmall),
    };
}
