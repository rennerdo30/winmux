using Avalonia;
using Avalonia.Controls;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

public sealed class ChromeSeparatorTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(1200)]
    public Task Caption_separator_spans_identity_toolbar_and_caption_buttons(double width) =>
        Headless.RunSync(() =>
        {
            var toolbar = Assert.IsType<Border>(ShellToolbar.Build(_ => { }, _ => { },
                (_, _) => { }, () => []));
            var window = new Window { Title = "WinMux" };
            var caption = Assert.IsType<Border>(TitleBar.Build(window, toolbar));
            caption.Measure(new Size(width, double.PositiveInfinity));
            caption.Arrange(new Rect(0, 0, width, caption.DesiredSize.Height));

            // One separator belongs to the outer caption. A second toolbar border would
            // produce an extra row only in the middle, as in the reported screenshots.
            Assert.Equal(new Thickness(0, 0, 0, 1), caption.BorderThickness);
            Assert.Equal(default(Thickness), toolbar.BorderThickness);
            Assert.Equal(width, caption.Bounds.Width);
            Assert.Equal(41, caption.Bounds.Height);
            Assert.Equal(40, toolbar.Bounds.Height);
            Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
        });
}
