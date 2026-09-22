using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Model;
using WinMux.Panes;
using WinMux.Shell.FileBrowser;

namespace WinMux.Shell.Tests;

/// <summary>
/// Dropping onto a real file-browser pane, through Avalonia's own drag-and-drop events.
///
/// The model tests prove what a transfer does; these prove that a drop reaches it — that the pane
/// accepts the payload, works out the target and the effect, and runs the transfer. Two panes side by
/// side, as a user has them.
/// </summary>
public sealed class FileBrowserDragDropTests : IDisposable
{
    private readonly string _left = Directory.CreateTempSubdirectory("winmux-drag-left-").FullName;
    private readonly string _right = Directory.CreateTempSubdirectory("winmux-drag-right-").FullName;

    public void Dispose()
    {
        foreach (var directory in new[] { _left, _right })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public Task Dropping_on_a_pane_moves_the_item_into_its_directory() => Headless.RunAsync(async () =>
    {
        var file = Path.Combine(_left, "moving.txt");
        File.WriteAllText(file, "payload");
        var (window, _, right) = await TwoPanesAsync();

        Drop(window, PointIn(right.View, 0.5, 0.8), [new FileBrowserClipboardEntry(file, false)], RawInputModifiers.None);
        await UntilAsync(() => File.Exists(Path.Combine(_right, "moving.txt")));

        // Same filesystem, no modifier: a move, as in Explorer.
        Assert.False(File.Exists(file));
        window.Close();
    });

    [Fact]
    public Task Ctrl_dropping_on_a_folder_row_copies_into_that_folder() => Headless.RunAsync(async () =>
    {
        var file = Path.Combine(_left, "copying.txt");
        File.WriteAllText(file, "payload");
        Directory.CreateDirectory(Path.Combine(_right, "inbox"));
        var (window, _, right) = await TwoPanesAsync();

        var list = right.View.GetVisualDescendants().OfType<ListBox>().Single();
        var inbox = list.Items.OfType<ListBoxItem>()
            .Single(row => row.Tag is FileBrowserNavigationItem { Name: "inbox" });
        window.UpdateLayout();
        var point = inbox.TranslatePoint(new Point(20, inbox.Bounds.Height / 2), window)!.Value;

        Drop(window, point, [new FileBrowserClipboardEntry(file, false)], RawInputModifiers.Control);
        await UntilAsync(() => File.Exists(Path.Combine(_right, "inbox", "copying.txt")));

        Assert.True(File.Exists(file), "Ctrl makes it a copy");
        Assert.False(File.Exists(Path.Combine(_right, "copying.txt")), "into the folder, not beside it");
        window.Close();
    });

    // ---- helpers ---------------------------------------------------------------------

    private async Task<(Window Window, IPaneRuntime Left, IPaneRuntime Right)> TwoPanesAsync()
    {
        var left = await CreateAsync(_left);
        var right = await CreateAsync(_right);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(right.View, 1);
        grid.Children.Add(left.View);
        grid.Children.Add(right.View);

        var window = new Window { Width = 1000, Height = 600, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, left, right);
    }

    private static async Task<IPaneRuntime> CreateAsync(string directory)
    {
        var provider = new FileBrowserPaneProvider(() => directory);
        return await provider.CreateAsync(new PaneProviderContext(PaneId.New(), "files", new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal) { ["current_directory"] = directory },
        }));
    }

    private static Point PointIn(Control control, double x, double y) =>
        control.TranslatePoint(new Point(control.Bounds.Width * x, control.Bounds.Height * y), TopLevel.GetTopLevel(control)!)!.Value;

    private static void Drop(Window window, Point point, IReadOnlyList<FileBrowserClipboardEntry> entries, RawInputModifiers modifiers)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(
            FileBrowserPaneRuntime.PayloadFormat,
            new FileBrowserDragPayload(SystemFileBrowserFileSystem.Instance, entries)));

        const DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move;
        window.DragDrop(point, RawDragEventType.DragEnter, transfer, allowed, modifiers);
        window.DragDrop(point, RawDragEventType.DragOver, transfer, allowed, modifiers);
        window.DragDrop(point, RawDragEventType.Drop, transfer, allowed, modifiers);
    }

    /// <summary>The drop starts a background transfer; wait for its result, with a ceiling.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var elapsed = 0; elapsed < 5000; elapsed += 25)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail("the drop did not complete within five seconds");
    }
}
