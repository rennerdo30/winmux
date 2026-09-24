using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using WinMux.Core.Settings;

namespace WinMux.Shell.Tests;

/// <summary>
/// The settings dialog, actually constructed.
///
/// <para>
/// Nothing built it until 2026-09-24, and the page had been shipping with its three file paths
/// wrapped into fragments — <c>C:</c> alone on one line, <c>ml</c> alone on the next — because a
/// right-aligned wrapping text block breaks a Windows path at its separators. It is the same class
/// of fault as every other one this suite exists for: it compiled, and only a person looking at the
/// screen could see it.
/// </para>
/// </summary>
public sealed class SettingsWindowTests
{
    private const string LongPath = @"C:\Users\somebody\AppData\Roaming\WinMux\settings.toml";

    private static SettingsWindow Build() => new(
        WinMuxSettings.Defaults,
        LaunchProfile.Defaults,
        @"C:\Users\somebody\AppData\Roaming\WinMux\session.toml",
        LongPath,
        @"C:\Users\somebody\AppData\Roaming\WinMux\profiles.toml");

    [Fact]
    public Task It_can_be_built() => Headless.RunSync(() =>
    {
        var window = Build();
        window.Show();

        Assert.Null(window.Result);
        Assert.NotEmpty(window.GetVisualDescendants().OfType<Button>());
    });

    [Fact]
    public Task A_file_path_is_shown_on_one_line_with_the_whole_of_it_on_the_tooltip() => Headless.RunSync(() =>
    {
        var window = Build();
        window.Show();

        var shown = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Single(t => t.Text == LongPath);

        Assert.Equal(TextWrapping.NoWrap, shown.TextWrapping);
        Assert.NotEqual(TextTrimming.None, shown.TextTrimming);
        Assert.Equal(LongPath, ToolTip.GetTip(shown));
    });

    [Fact]
    public Task Every_profile_is_listed() => Headless.RunSync(() =>
    {
        var window = Build();
        window.Show();

        var list = window.GetVisualDescendants().OfType<ListBox>().Single();

        Assert.Equal(LaunchProfile.Defaults.Count, list.ItemCount);
    });

    [Fact]
    public Task The_dialog_can_be_made_taller() => Headless.RunSync(() =>
    {
        // The page is over 2,000px of cards. It was fixed at a 640px viewport with CanResize false,
        // so there was no way to see more of it at once on any monitor.
        var window = Build();

        Assert.True(window.CanResize);
        Assert.True(window.Height > 640);
    });

    [Fact]
    public Task Saving_without_touching_anything_returns_what_it_opened_with() => Headless.RunSync(() =>
    {
        // A settings dialog that quietly rewrites a value the user never touched is the worst kind,
        // because nothing on screen said it would.
        var window = Build();
        window.Show();

        var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save");
        save.Command?.Execute(null);
        ((Avalonia.Interactivity.Interactive)save).RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(WinMuxSettings.Defaults, window.Result);
    });
}
