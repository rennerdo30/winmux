using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WinMux.Shell.FileBrowser;

/// <summary>
/// What a file-browser pane says about itself: where it is, whether it is connected, and what each
/// entry is.
///
/// <para>
/// <b>Where it is.</b> Two browsers side by side looked identical whether one was the local disk and
/// the other a server on another continent — a pane inside a split has no tab strip, so its title
/// never shows. The location badge says it on the pane itself: <i>This PC</i>, <i>Network share</i>,
/// or the protocol and the account on the server, with plain FTP marked as unencrypted.
/// </para>
///
/// <para>
/// <b>Whether it is connected.</b> A refused password used to arrive as three run-on sentences in the
/// status line with no way forward but closing the pane. It is now one sentence in a banner, with a
/// button that asks for the password again.
/// </para>
///
/// <para>
/// <b>What each entry is.</b> Name, date modified, type and size, as in Explorer, sortable by clicking
/// a header. Columns give way from the right as the pane narrows, so a thin pane still shows names.
/// </para>
/// </summary>
internal sealed partial class FileBrowserPaneRuntime
{
    private const double ModifiedWidth = 150;
    private const double TypeWidth = 150;
    private const double SizeWidth = 90;

    private readonly Border _location = new()
    {
        Padding = new Thickness(10, 0),
        Margin = new Thickness(6, 0, 0, 0),
        CornerRadius = Chrome.Palette.ControlRadius,
        Background = Chrome.Palette.RaisedBrush,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly TextBlock _locationText = new() { VerticalAlignment = VerticalAlignment.Center };

    private readonly Border _banner = new()
    {
        IsVisible = false,
        Padding = new Thickness(12, 8),
        Margin = new Thickness(6, 0, 6, 6),
        CornerRadius = Chrome.Palette.ControlRadius,
        Background = Chrome.Palette.RaisedBrush,
        BorderBrush = Chrome.Palette.DangerBrush,
        BorderThickness = new Thickness(1),
    };

    private readonly TextBlock _connectionText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Inset by the rows' own padding, so each heading sits over its column.
    private readonly Grid _header = new() { Margin = new Thickness(8, 0, 8, 2) };
    private readonly List<Grid> _rowGrids = [];
    private readonly Dictionary<FileBrowserSortColumn, Button> _headerButtons = [];

    /// <summary>Which detail columns fit, from the list's current width.</summary>
    private (bool Modified, bool Type, bool Size) _columns = (true, true, true);

    private void BuildChrome()
    {
        _location.Child = _locationText;

        var retry = new Button { Content = "Try again", VerticalAlignment = VerticalAlignment.Center };
        retry.Classes.Add(Chrome.Theme.ToolbarButton);
        retry.Click += (_, _) => _ = ReconnectAsync();
        var bannerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(retry, 1);
        bannerGrid.Children.Add(_connectionText);
        bannerGrid.Children.Add(retry);
        _banner.Child = bannerGrid;

        DefineColumns(_header);
        AddHeader(FileBrowserSortColumn.Name, "Name", 0, HorizontalAlignment.Left);
        AddHeader(FileBrowserSortColumn.Modified, "Date modified", 1, HorizontalAlignment.Left);
        AddHeader(FileBrowserSortColumn.Type, "Type", 2, HorizontalAlignment.Left);
        AddHeader(FileBrowserSortColumn.Size, "Size", 3, HorizontalAlignment.Right);
        _rowGrids.Add(_header);

        _list.SizeChanged += (_, e) => FitColumns(e.NewSize.Width);
    }

    private void AddHeader(FileBrowserSortColumn column, string title, int index, HorizontalAlignment alignment)
    {
        var button = new Button
        {
            Content = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = alignment,
            Padding = new Thickness(index == 0 ? 24 : 8, 4, 8, 4),
            Foreground = Chrome.Palette.MutedTextBrush,
        };
        button.Classes.Add(Chrome.Theme.ToolbarButton);
        ToolTip.SetTip(button, $"Sort by {title.ToLowerInvariant()}");
        button.Click += (_, _) =>
        {
            if (_model is null) return;
            _model.SortBy(column);
            RenderModel();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        Grid.SetColumn(button, index);
        _header.Children.Add(button);
        _headerButtons[column] = button;
    }

    /// <summary>Name takes what is left; the details have fixed widths so the rows line up.</summary>
    private void DefineColumns(Grid grid)
    {
        grid.ColumnDefinitions = new ColumnDefinitions
        {
            new ColumnDefinition(GridLength.Star),
            new ColumnDefinition(new GridLength(_columns.Modified ? ModifiedWidth : 0)),
            new ColumnDefinition(new GridLength(_columns.Type ? TypeWidth : 0)),
            new ColumnDefinition(new GridLength(_columns.Size ? SizeWidth : 0)),
        };
    }

    /// <summary>
    /// Give the name room first: type goes when the pane gets narrow, then the date, then the size —
    /// the order in which each is least missed.
    /// </summary>
    private void FitColumns(double width)
    {
        var fit = (Modified: width >= 440, Type: width >= 600, Size: width >= 300);
        if (fit == _columns) return;

        _columns = fit;
        foreach (var grid in _rowGrids) DefineColumns(grid);
    }

    private void RenderHeader()
    {
        if (_model is null) return;
        foreach (var (column, button) in _headerButtons)
        {
            var title = column switch
            {
                FileBrowserSortColumn.Modified => "Date modified",
                FileBrowserSortColumn.Type => "Type",
                FileBrowserSortColumn.Size => "Size",
                _ => "Name",
            };

            button.Content = column == _model.SortColumn
                ? $"{title} {(_model.SortDescending ? "↓" : "↑")}"
                : title;
        }
    }

    // ---- rows -------------------------------------------------------------------------------

    private sealed record RowView(FileBrowserNavigationItem Item, Image Icon, TextBlock Type);

    private Control RowContent(FileBrowserNavigationItem item, out RowView view)
    {
        var icon = new Image
        {
            Width = IconSize,
            Height = IconSize,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = item.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        var type = Detail(item.IsDirectory ? "Folder" : Extension(item.Name), 2);
        var grid = new Grid { Children = { name } };
        DefineColumns(grid);
        grid.Children.Add(Detail(FormatModified(item.Modified), 1));
        grid.Children.Add(type);
        grid.Children.Add(Detail(FormatSize(item), 3, HorizontalAlignment.Right));
        _rowGrids.Add(grid);

        view = new RowView(item, icon, type);
        return grid;
    }

    private static TextBlock Detail(string text, int column, HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = Chrome.Palette.MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0),
        };
        Grid.SetColumn(block, column);
        return block;
    }

    /// <summary>Until the system's name for the type arrives: "TXT file", as Explorer says for an unknown one.</summary>
    private static string Extension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length > 1 ? $"{extension[1..].ToUpperInvariant()} file" : "File";
    }

    private static string FormatModified(DateTimeOffset? modified) =>
        modified is { } value ? value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>
    /// Sizes the way Explorer's details view shows them: kilobytes, rounded up, so a one-byte file is
    /// "1 KB" rather than "0 KB" — and megabytes and gigabytes once kilobytes stop being readable.
    /// </summary>
    internal static string FormatSize(FileBrowserNavigationItem item)
    {
        if (item.IsDirectory || item.Size is not { } bytes) return string.Empty;
        if (bytes == 0) return "0 KB";
        if (bytes < 1024L * 1024) return $"{Math.Ceiling(bytes / 1024.0):N0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):N1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):N1} GB";
    }

    /// <summary>
    /// Fill in the icons and type names for a freshly rendered listing, off the UI thread. A local file
    /// with its own icon — a program, a shortcut — is read from disk, and a folder of a few hundred of
    /// those should not hold up the list appearing.
    /// </summary>
    private void LoadIcons(IReadOnlyList<RowView> rows)
    {
        var icons = PlatformServices.FileIcons;
        if (!icons.IsAvailable || rows.Count == 0) return;

        var generation = Interlocked.Increment(ref _iconGeneration);
        var local = IsLocalFileSystem;
        var requests = rows.Select(row => (row.Item.Name, row.Item.IsDirectory, row.Item.Path)).ToArray();

        _ = Task.Run(() =>
        {
            var pictures = new byte[]?[requests.Length];
            var types = new string?[requests.Length];
            for (var index = 0; index < requests.Length; index++)
            {
                if (Volatile.Read(ref _iconGeneration) != generation) return;
                var (name, isDirectory, path) = requests[index];
                pictures[index] = icons.GetIconPng(name, isDirectory, LocalPathForIcon(local, path), 32);
                types[index] = icons.GetTypeName(name, isDirectory);
            }

            Dispatcher.UIThread.Post(() =>
            {
                // A newer listing has replaced these rows; its own load will fill them in.
                if (Volatile.Read(ref _iconGeneration) != generation || Volatile.Read(ref _disposed) != 0) return;
                for (var index = 0; index < rows.Count; index++)
                {
                    if (pictures[index] is { } png) rows[index].Icon.Source = Decode(png);
                    if (types[index] is { } type) rows[index].Type.Text = type;
                }
            });
        });
    }

    // ---- location and connection ------------------------------------------------------------

    /// <summary>Say what this pane is a view of. Called whenever the location or connection changes.</summary>
    private void UpdateLocation()
    {
        string text;
        string tip;
        IBrush foreground = Chrome.Palette.TextBrush;

        if (_target is { } target)
        {
            var port = target.Port > 0 && target.Port != DefaultPort(target) ? $":{target.Port}" : string.Empty;
            var who = target.User.Length > 0 ? $"{target.User}@{target.Host}{port}" : $"{target.Host}{port}";
            text = $"{target.Scheme.ToUpperInvariant()} · {who}";
            tip = $"A server: {target.Display}{port}";

            if (_fileSystem is Remote.FtpFileBrowserFileSystem ftp && _model is not null && !ftp.IsEncrypted)
            {
                // Said on the pane, not only when signing in: the files go in clear text too.
                text += " · not encrypted";
                tip += ". This connection is plain FTP: the password and every file travel unencrypted.";
                foreground = Chrome.Palette.DangerBrush;
            }
        }
        else
        {
            var directory = _model?.CurrentDirectory ?? _fallbackDirectory;
            var share = directory.StartsWith(@"\\", StringComparison.Ordinal);
            text = share ? "Network share" : "This PC";
            tip = share ? "A Windows network share, reached through Windows itself" : "This computer's own disks";
        }

        _locationText.Text = text;
        _locationText.Foreground = foreground;
        ToolTip.SetTip(_location, tip);
    }

    private static int DefaultPort(Remote.RemoteFileBrowserTarget target) =>
        target.Scheme == Remote.RemoteFileBrowserTarget.Ftp
            ? WinMux.Core.Settings.RemoteConnection.DefaultFtpPort
            : WinMux.Core.Settings.RemoteConnection.DefaultSftpPort;

    private void ShowConnectionProblem(string reason)
    {
        var who = _target?.Display ?? "the server";
        _connectionText.Text = $"Not connected to {who}. {Sentence(reason)}";
        _banner.IsVisible = true;
        _list.Items.Clear();
        _rowGrids.RemoveRange(1, _rowGrids.Count - 1);
        _status.Text = string.Empty;
    }

    private void HideConnectionProblem() => _banner.IsVisible = false;

    /// <summary>Library messages start in lower case and stop without a full stop; a banner should not.</summary>
    private static string Sentence(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return text;
        text = char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        return text.EndsWith('.') ? text : text + ".";
    }
}
