using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Platform;

namespace WinMux.Shell.FileBrowser;

/// <summary>
/// What a drag between file-browser panes carries: the paths, and the filesystem they are paths on.
///
/// The filesystem is the whole point. A drag from an SFTP pane to a local one is a download, and the
/// drop can only know that because the payload says where the paths live — the same reason the
/// clipboard carries it (ADR 0020's 2026-09-23 addendum).
/// </summary>
internal sealed record FileBrowserDragPayload(
    IFileBrowserFileSystem FileSystem,
    IReadOnlyList<FileBrowserClipboardEntry> Entries);

/// <summary>
/// Dragging, dropping and icons.
///
/// <para>
/// <b>Between panes</b> a drag carries a <see cref="FileBrowserDragPayload"/> in an in-process format,
/// so any pane can drop into any other whatever the two filesystems are: local to SFTP, FTP to local,
/// one server to another, an SMB share (which is a local path) to anywhere. The drop goes through the
/// same <see cref="FileBrowserModel.Transfer"/> that paste uses, so it has the same rules — nothing
/// overwritten, a move removes the original only after a complete copy.
/// </para>
///
/// <para>
/// <b>With Explorer</b>, a drag from a local pane also carries the files themselves, so it can be
/// dropped on the desktop or an Explorer window; and files dragged in from Explorer can be dropped on
/// any pane, remote ones included — an upload. A drag from a <i>remote</i> pane cannot go to Explorer:
/// that would need Explorer's virtual-file protocol, which asks for the bytes of a file that does not
/// exist on disk yet, and Avalonia offers no way to answer it.
/// </para>
///
/// <para>
/// The effect follows Explorer: within one filesystem a drag moves, across two it copies; Ctrl forces
/// a copy and Shift a move. Dropping on a folder row puts the items in that folder.
/// </para>
/// </summary>
internal sealed partial class FileBrowserPaneRuntime
{
    internal static readonly DataFormat<FileBrowserDragPayload> PayloadFormat =
        DataFormat.CreateInProcessFormat<FileBrowserDragPayload>("winmux-file-browser-items");

    /// <summary>How far the pointer travels, in device-independent pixels, before a press is a drag.</summary>
    private const double DragThreshold = 6;

    /// <summary>Row icon edge, in device-independent pixels.</summary>
    private const double IconSize = 16;

    /// <summary>
    /// One decoded bitmap per distinct icon. The platform caches its PNG bytes per file type, so the
    /// same array comes back for every <c>.txt</c>; keying by that reference decodes each type once.
    /// </summary>
    private static readonly Dictionary<byte[], Bitmap?> DecodedIcons = new(ReferenceEqualityComparer.Instance);

    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private ListBoxItem? _pressedRow;
    private bool _clickCollapsesSelection;
    private bool _dragging;
    private ListBoxItem? _dropRow;
    private IBrush? _dropRowBackground;
    private int _iconGeneration;

    private void InitializeDragDrop()
    {
        _list.SelectionMode = SelectionMode.Multiple;

        // Tunnel, so this runs before the ListBox's own handling: pressing on a row that is already
        // part of a multiple selection must not collapse the selection to that row, or there would be
        // no way to drag more than one item. The collapse happens on release instead, if no drag did.
        _list.AddHandler(InputElement.PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        _list.AddHandler(InputElement.PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        _list.AddHandler(InputElement.PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(_root, true);
        _root.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _root.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
        _root.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ---- icons ------------------------------------------------------------------------------

    /// <summary>
    /// A path worth reading an icon from, or null to use the type icon. Null for anything remote, and
    /// for a network share too: a per-file icon over SMB costs a round trip per row.
    /// </summary>
    private static string? LocalPathForIcon(bool local, string path) =>
        local && !path.StartsWith(@"\\", StringComparison.Ordinal) ? path : null;

    private static Bitmap? Decode(byte[] png)
    {
        if (DecodedIcons.TryGetValue(png, out var cached)) return cached;

        Bitmap? bitmap;
        try
        {
            using var stream = new MemoryStream(png);
            bitmap = new Bitmap(stream);
        }
        catch (Exception)
        {
            // A picture that will not decode is not worth a message; the row keeps its name.
            bitmap = null;
        }

        DecodedIcons[png] = bitmap;
        return bitmap;
    }

    private bool IsLocalFileSystem => _model?.FileSystem is SystemFileBrowserFileSystem;

    // ---- dragging out -----------------------------------------------------------------------

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressArgs = null;
        _pressedRow = null;
        _clickCollapsesSelection = false;

        var point = e.GetCurrentPoint(_list);
        if (!point.Properties.IsLeftButtonPressed || _model is null) return;

        var row = RowAt(e.Source);
        if (row is null) return;

        _pressArgs = e;
        _pressPoint = point.Position;
        _pressedRow = row;

        // Only when several rows are selected and this is one of them, and only for a plain single
        // click: a double-click, or a Ctrl/Shift click that edits the selection, is the list's own.
        if (_list.SelectedItems?.Count > 1 && row.IsSelected && e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _clickCollapsesSelection = true;
            FocusList();
        }
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is not { } press || _dragging || _model is not { } model) return;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            _pressArgs = null;
            return;
        }

        var delta = e.GetPosition(_list) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        _pressArgs = null;
        _clickCollapsesSelection = false;

        // A press on a row that was not selected has selected it by now (the list handles the press
        // itself in that case), so the selection is what gets dragged.
        var entries = model.SelectedEntries;
        if (entries.Count == 0) return;

        _dragging = true;
        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(PayloadFormat, new FileBrowserDragPayload(model.FileSystem, entries)));

            // Local files also travel as files, which is what Explorer and the desktop understand.
            if (IsLocalFileSystem && TopLevel.GetTopLevel(_root)?.StorageProvider is { } storage)
            {
                foreach (var entry in entries)
                {
                    IStorageItem? item = entry.IsDirectory
                        ? await storage.TryGetFolderFromPathAsync(entry.Path)
                        : await storage.TryGetFileFromPathAsync(entry.Path);
                    if (item is not null) transfer.Add(DataTransferItem.CreateFile(item));
                }
            }

            var effect = await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy | DragDropEffects.Move);

            // A drop on Explorer may have moved the files away; a drop on another pane announces its
            // own changes when it finishes. Either way this listing may be out of date.
            if (effect != DragDropEffects.None && Volatile.Read(ref _disposed) == 0)
            {
                _ = RunNavigationAsync((m, token) => { m.Refresh(token, reportLostSelection: false); return true; });
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The drag machinery is the platform's; a failure in it must not take the pane with it.
            _status.Text = "The drag could not be started: " + ex.Message;
        }
        finally
        {
            _dragging = false;
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_clickCollapsesSelection && _pressedRow is { } row && !_dragging)
        {
            // A plain click on one row of a multiple selection, with no drag: now it selects that row
            // alone, as Explorer does.
            _list.SelectedItems?.Clear();
            row.IsSelected = true;
            _list.SelectedItem = row;
        }

        _pressArgs = null;
        _pressedRow = null;
        _clickCollapsesSelection = false;
    }

    private static ListBoxItem? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    // ---- dropping in ------------------------------------------------------------------------

    /// <summary>What a drag over this pane would do, worked out once for DragOver and again for Drop.</summary>
    private readonly record struct DropPlan(
        IFileBrowserFileSystem From,
        IReadOnlyList<FileBrowserClipboardEntry> Entries,
        string? TargetDirectory,
        ListBoxItem? TargetRow,
        DragDropEffects Effect);

    private DropPlan? PlanDrop(DragEventArgs e)
    {
        if (_model is not { } model || !model.CanModify) return null;

        IFileBrowserFileSystem from;
        IReadOnlyList<FileBrowserClipboardEntry> entries;
        var fromExplorer = false;

        if (e.DataTransfer.TryGetValue(PayloadFormat) is { } payload)
        {
            from = payload.FileSystem;
            entries = payload.Entries;
        }
        else if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            // From Explorer, the desktop, or anything else that drags files. Only real paths count;
            // a virtual item from a shell namespace extension has none and is skipped.
            from = SystemFileBrowserFileSystem.Instance;
            entries = files
                .Select(file => file.TryGetLocalPath())
                .OfType<string>()
                .Select(path => new FileBrowserClipboardEntry(path, Directory.Exists(path)))
                .ToArray();
            fromExplorer = true;
        }
        else
        {
            return null;
        }

        if (entries.Count == 0) return null;

        // Onto a folder row: into that folder — unless the folder is one of the things being dragged.
        var row = RowAt(e.Source);
        string? target = null;
        if (row?.Tag is FileBrowserNavigationItem { IsDirectory: true } folder &&
            !(ReferenceEquals(from, model.FileSystem) &&
              entries.Any(entry => from.PathComparer.Equals(entry.Path, folder.Path))))
        {
            target = folder.Path;
        }
        else
        {
            row = null;
        }

        var sameFileSystem = ReferenceEquals(from, model.FileSystem);
        var effect = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move
            // Explorer's rule, and the safe default from outside: a drag from Explorer copies, since
            // WinMux cannot tell whether the source is on the same volume the way Explorer can.
            : sameFileSystem && !fromExplorer ? DragDropEffects.Move
            : DragDropEffects.Copy;

        effect &= e.DragEffects;
        if (effect == DragDropEffects.None) return null;

        // A move onto the directory everything already lives in does nothing; say so with the cursor.
        var destination = target ?? model.CurrentDirectory;
        if (sameFileSystem && effect == DragDropEffects.Move &&
            entries.All(entry => from.PathComparer.Equals(from.GetParentDirectory(entry.Path) ?? "", destination)))
        {
            return null;
        }

        return new DropPlan(from, entries, target, row, effect);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var plan = PlanDrop(e);
        e.DragEffects = plan?.Effect ?? DragDropEffects.None;
        e.Handled = true;
        ShowDropHighlight(plan);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var plan = PlanDrop(e);
        ClearDropHighlight();
        e.Handled = true;

        if (plan is not { } drop)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = drop.Effect;
        var isMove = drop.Effect == DragDropEffects.Move;

        // Not awaited: the platform's drag loop is waiting for this handler to return, and a copy from
        // a server can take minutes. The pane shows progress in its status line meanwhile.
        var progress = new Progress<string>(message =>
        {
            if (Volatile.Read(ref _disposed) == 0) _status.Text = message;
        });
        _ = RunNavigationAsync(
            (model, token) => model.Transfer(drop.From, drop.Entries, drop.TargetDirectory, isMove, token, progress),
            isMove ? "Moving…" : "Copying…");
    }

    private void ShowDropHighlight(DropPlan? plan)
    {
        var row = plan?.TargetRow;
        if (!ReferenceEquals(row, _dropRow))
        {
            if (_dropRow is not null) _dropRow.Background = _dropRowBackground;
            _dropRow = row;
            if (row is not null)
            {
                _dropRowBackground = row.Background;
                row.Background = Chrome.Palette.AccentMutedBrush;
            }
        }

        // The pane itself is the target when no folder row is: outline it, so it is clear which of two
        // side-by-side panes the drop will land in.
        _list.BorderBrush = plan is not null && row is null ? Chrome.Palette.AccentBrush : null;
        _list.BorderThickness = plan is not null && row is null ? new Thickness(1) : default;
    }

    private void ClearDropHighlight() => ShowDropHighlight(null);
}
