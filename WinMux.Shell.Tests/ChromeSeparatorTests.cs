using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

public sealed class ChromeSeparatorTests
{
    [Fact]
    public Task Root_caption_tabs_have_no_line_above_the_content() => Headless.RunSync(() =>
    {
        var pane = Pane.Terminal("first");
        var stack = new StackNode([new LeafNode(pane), new LeafNode(Pane.Terminal("second"))]);
        var strip = new WinMux.Core.Layout.TabStrip(stack,
            new WinMux.Core.Layout.Rect(0, 0, 800, 40), TabStripPlacement.Top);
        var commands = new TabStripCommands(_ => { }, _ => { }, _ => Task.CompletedTask, _ => { },
            (_, _, _) => { }, _ => { }, (_, _) => { }, _ => { });

        var captionBand = Assert.IsType<Border>(TabStripView.Build(strip, pane.Id, commands,
            isCaption: true));
        var nestedBand = Assert.IsType<Border>(TabStripView.Build(strip, pane.Id, commands));
        Assert.Equal(default(Thickness), captionBand.BorderThickness);
        Assert.Equal(new Thickness(0, 0, 0, 1), nestedBand.BorderThickness);
        Assert.Same(Palette.TabStripBrush, captionBand.Background);
    });

    [Theory]
    [InlineData(400)]
    [InlineData(1200)]
    public Task Caption_and_buttons_share_the_tab_strip_fill_without_a_separator(double width) =>
        Headless.RunSync(() =>
        {
            var toolbar = Assert.IsType<Border>(ShellToolbar.Build(_ => { }, _ => { },
                (_, _) => { }, () => []));
            var window = new Window { Title = "WinMux" };
            var caption = Assert.IsType<Border>(TitleBar.Build(window, toolbar));
            window.Styles.Add(Theme.Build());
            window.Content = caption;
            window.Show();
            caption.Measure(new Size(width, double.PositiveInfinity));
            caption.Arrange(new Avalonia.Rect(0, 0, width, caption.DesiredSize.Height));

            Assert.Same(Palette.TabStripBrush, caption.Background);
            Assert.Equal(3, caption.GetVisualDescendants().OfType<Button>()
                .Count(button => button.Classes.Contains(Theme.CaptionButton) &&
                    button.GetVisualDescendants().OfType<ContentPresenter>()
                        .Any(presenter => ReferenceEquals(presenter.Background, caption.Background))));
            Assert.Same(Brushes.Transparent, toolbar.Background);
            Assert.Equal(default(Thickness), caption.BorderThickness);
            Assert.Equal(default(Thickness), toolbar.BorderThickness);
            Assert.Equal(width, caption.Bounds.Width);
            Assert.Equal(40, caption.Bounds.Height);
            Assert.Equal(40, toolbar.Bounds.Height);
            Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
            window.Close();
        });
}
