using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WinMux.Core.Update;
using WinMux.Shell.Actions;
using WinMux.Shell.Chrome;
using WinMux.Shell.Update;

namespace WinMux.Shell.Tests;

public sealed class HelpMenuTests
{
    [Fact]
    public Task About_and_update_commands_remain_reachable_with_caption_tabs() =>
        Headless.RunAsync(async () =>
        {
            var package = new ReleaseAsset("WinMux-0.8.1-win-x64.zip", "https://example.invalid/a", 1);
            var sums = new ReleaseAsset("checksums.txt", "https://example.invalid/s", 1);
            var release = new Release(ReleaseVersion.Parse("0.8.1"), false, "notes",
                "https://example.invalid/release", [package, sums]);
            var updates = new UpdateController(
                _ => Task.FromResult(new UpdateDecision(UpdateOutcome.UpdateAvailable, release, package, sums)),
                (_, _, _) => Task.FromResult(new UpdateStageResult(true, "staged", "ready")));
            var dispatched = new List<string>();
            var help = ShellToolbar.BuildHelpButton(dispatched.Add, updates);
            var toolbar = ShellToolbar.Build(dispatched.Add, _ => { }, (_, _) => { }, () => []);
            var window = new Window { Width = 800, Height = 100 };
            var caption = TitleBar.Build(window, toolbar, out var tabs, help);
            window.Content = caption;
            toolbar.IsVisible = false;
            tabs.Content = new Border();
            tabs.IsVisible = true;
            window.Show();
            try
            {
                Assert.Contains(help, caption.GetVisualDescendants());
                var items = help.ContextMenu!.Items.OfType<MenuItem>().ToArray();
                items.Single(item => Equals(item.Header, "About WinMux"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                items.Single(item => Equals(item.Header, "Check for updates"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal([ShellActionNames.ShowAbout, ShellActionNames.CheckForUpdates], dispatched);

                var badge = help.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("update-badge"));
                Assert.False(badge.IsVisible);
                await updates.CheckAsync();
                Assert.True(badge.IsVisible);
            }
            finally { window.Close(); }
        });
}
