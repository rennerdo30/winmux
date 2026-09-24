using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Connections;
using WinMux.Core.Settings;
using WinMux.Platform;
using WinMux.Shell.Chrome;
using WinMux.Shell.Connections;

namespace WinMux.Shell;

/// <summary>
/// The saved connections of every tool on this machine, in one tree.
///
/// <para>
/// Six readers shipped before this did, and until it existed they were a capability with no
/// interface — which CLAUDE.md section 5a says is not a feature but a note to the author. This is
/// the interface.
/// </para>
///
/// <para>
/// The right-hand side is the part that justifies the whole design: every setting is shown with
/// <em>where it came from</em>, so a server connecting as the wrong user tells you which folder to
/// go and change. mRemoteNG and Remote Desktop Connection Manager both do this, and it is most of
/// why their inheritance is usable rather than merely present (ADR 0025).
/// </para>
/// </summary>
internal sealed class ConnectionsWindow : Window
{
    private readonly ListBox _tree = new();
    private readonly StackPanel _details = new() { Spacing = 4 };
    private readonly TextBlock _heading;
    private readonly TextBlock _note;
    private readonly Button _open;
    private readonly Button _import;
    private readonly Button _passwords;

    private readonly List<Row> _rows = [];
    private readonly HashSet<ConnectionFolder> _collapsed = [];
    private readonly List<ConnectionSourceResult> _results;
    private readonly ICredentialStore? _credentials;

    /// <summary>Set when the user chose a connection to open, rather than closing the window.</summary>
    public LaunchProfile? Chosen { get; private set; }

    /// <summary>Profiles the user asked to keep, when they imported a folder or a host.</summary>
    public IReadOnlyList<LaunchProfile> Imported => _imported;

    private readonly List<LaunchProfile> _imported = [];

    public ConnectionsWindow(IReadOnlyList<ConnectionSourceResult> results, ICredentialStore? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        _results = [.. results];
        _credentials = credentials;

        Title = "Saved connections";
        Width = 900;
        Height = 620;
        MinWidth = 640;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        AppIcon.Apply(this);

        _heading = new TextBlock
        {
            Text = "Nothing selected",
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TextBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        _note = new TextBlock
        {
            FontSize = Palette.CaptionSize,
            LineHeight = 16,
            Foreground = Palette.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        _tree.Background = Palette.SurfaceBrush;
        _tree.CornerRadius = Palette.ControlRadius;
        _tree.SelectionChanged += (_, _) => ShowSelected();
        _tree.DoubleTapped += (_, _) => OpenSelected();

        _open = Dialog("Open in a pane", OpenSelected);
        _import = Dialog("Import into WinMux", ImportSelected);
        _passwords = Dialog("Save passwords…", () => _ = SavePasswordsAsync());

        var close = Dialog("Close", Close);
        close.IsCancel = true;

        Content = Build(close);
        Render();

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private Control Build(Button close)
    {
        var left = new DockPanel { LastChildFill = true };
        var caption = new TextBlock
        {
            Text = _results.Count == 0
                ? "No saved connections were found on this machine."
                : "From " + string.Join(", ", _results.Select(result => result.Source.DisplayName)),
            Foreground = Palette.MutedTextBrush,
            FontSize = Palette.CaptionSize,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, Palette.GapSmall),
        };
        DockPanel.SetDock(caption, Dock.Top);
        left.Children.Add(caption);
        left.Children.Add(_tree);

        var right = new StackPanel { Spacing = Palette.GapSmall };
        right.Children.Add(_heading);
        right.Children.Add(_note);
        right.Children.Add(_details);

        var body = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(1.1, GridUnitType.Star), new ColumnDefinition(1, GridUnitType.Star)],
            Margin = new Thickness(Palette.GapLarge),
        };
        Grid.SetColumn(left, 0);
        var scroller = new ScrollViewer
        {
            Content = right,
            Margin = new Thickness(Palette.GapLarge, 0, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(scroller, 1);
        body.Children.Add(left);
        body.Children.Add(scroller);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Palette.GapSmall,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _passwords, _import, _open, close },
        };

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Palette.GapLarge, Palette.GapMedium),
            Child = buttons,
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        return root;
    }

    private static Button Dialog(string label, Action invoke)
    {
        var button = new Button { Content = label, Classes = { Chrome.Theme.DialogButton } };
        button.Click += (_, _) => invoke();
        return button;
    }

    /// <summary>One line in the tree: a source, a folder or a host, with the depth to indent by.</summary>
    private sealed record Row(ConnectionSourceResult Result, ConnectionNode? Node, int Depth, string? Problem);

    private void Render()
    {
        var selected = Selected()?.Node;

        _rows.Clear();
        foreach (var result in _results)
        {
            _rows.Add(new Row(result, result.Root, 0, result.Problem));
            if (result.Root is { } root && !_collapsed.Contains(root)) AddChildren(result, root, 1);
        }

        _tree.ItemsSource = _rows.Select(ItemFor).ToArray();

        var at = _rows.FindIndex(row => ReferenceEquals(row.Node, selected));
        _tree.SelectedIndex = at >= 0 ? at : (_rows.Count > 0 ? 0 : -1);
    }

    private void AddChildren(ConnectionSourceResult result, ConnectionFolder folder, int depth)
    {
        foreach (var child in folder.Children)
        {
            _rows.Add(new Row(result, child, depth, null));
            if (child is ConnectionFolder nested && !_collapsed.Contains(nested))
            {
                AddChildren(result, nested, depth + 1);
            }
        }
    }

    private ListBoxItem ItemFor(Row row)
    {
        var text = new StackPanel { Margin = new Thickness(2) };

        var title = row.Node?.Name ?? row.Result.Source.DisplayName;

        // The tool's name, and the root's own name only when it says something else. Several of
        // these formats call their root after the tool, and "PuTTY - PuTTY" tells nobody anything.
        if (row.Depth == 0)
        {
            var tool = row.Result.Source.DisplayName;
            title = string.IsNullOrWhiteSpace(title) || title.Equals(tool, StringComparison.OrdinalIgnoreCase)
                ? tool
                : $"{tool} — {title}";
        }

        text.Children.Add(new TextBlock
        {
            Text = (row.Node is ConnectionFolder folder ? (_collapsed.Contains(folder) ? "▸ " : "▾ ") : "")
                   + (string.IsNullOrWhiteSpace(title) ? "(unnamed)" : title),
            Foreground = Palette.TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var caption = row.Problem ?? (row.Node is ConnectionEntry entry ? Summarise(entry) : null);
        if (caption is not null)
        {
            text.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = Palette.CaptionSize,
                Foreground = row.Problem is null ? Palette.FaintTextBrush : Palette.DangerBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        return new ListBoxItem
        {
            Tag = row,
            Content = new Border { Margin = new Thickness(row.Depth * 16, 0, 0, 0), Child = text },
        };
    }

    private static string Summarise(ConnectionEntry entry)
    {
        var protocol = ConnectionResolver.Protocol(entry).Value;
        var user = ConnectionResolver.User(entry).Value;
        var host = ConnectionResolver.Host(entry).Value;
        var port = ConnectionResolver.Port(entry).Value;

        var target = string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}";
        if (port is > 0) target += $":{port}";
        return protocol == ConnectionProtocol.Unknown ? target ?? "" : $"{protocol} · {target}";
    }

    private Row? Selected() => (_tree.SelectedItem as ListBoxItem)?.Tag as Row;

    private void ShowSelected()
    {
        _details.Children.Clear();

        var row = Selected();
        if (row is null)
        {
            _heading.Text = "Nothing selected";
            _note.Text = string.Empty;
            _open.IsEnabled = _import.IsEnabled = _passwords.IsEnabled = false;
            return;
        }

        if (row.Problem is { } problem)
        {
            _heading.Text = row.Result.Source.DisplayName;
            _note.Text = problem;
            _open.IsEnabled = _import.IsEnabled = _passwords.IsEnabled = false;
            return;
        }

        var node = row.Node!;
        _heading.Text = node.Name.Length > 0 ? node.Name : "(unnamed)";
        _note.Text = $"{node.Path}\n{row.Result.Source.DisplayName} — {row.Result.Source.Location}";

        _open.IsEnabled = node is ConnectionEntry entry && ConnectionCatalog.ToProfile(entry) is not null;
        _import.IsEnabled = node is ConnectionFolder or ConnectionEntry;
        _passwords.IsEnabled = _credentials is not null;

        Fact("Protocol", ConnectionResolver.Protocol(node), node);
        Fact("Host", ConnectionResolver.Host(node), node);
        Fact("Port", ConnectionResolver.Port(node), node);
        Fact("User", ConnectionResolver.User(node), node);
        Fact("Domain", ConnectionResolver.Domain(node), node);
        Fact("Gateway", ConnectionResolver.Gateway(node), node);
        Fact("Key file", ConnectionResolver.Identity(node), node);
        Fact("Directory", ConnectionResolver.RemoteDirectory(node), node);

        if (node is ConnectionFolder container)
        {
            _details.Children.Add(new TextBlock
            {
                Text = $"{container.Entries().Count()} connection(s) below this.",
                FontSize = Palette.CaptionSize,
                Foreground = Palette.MutedTextBrush,
                Margin = new Thickness(0, Palette.GapSmall, 0, 0),
            });
        }
    }

    /// <summary>
    /// One setting, with the folder it came from when it is not this node's own. The second half is
    /// the point: a value you cannot locate is a value you cannot change.
    /// </summary>
    private void Fact<T>(string label, ResolvedSetting<T> resolved, ConnectionNode asked)
    {
        if (!resolved.IsSet) return;
        if (resolved.Value is int number && number <= 0) return;
        if (resolved.Value is string text && text.Length == 0) return;

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(110, GridUnitType.Pixel), new ColumnDefinition(GridLength.Star)],
        };

        var name = new TextBlock
        {
            Text = label,
            Foreground = Palette.MutedTextBrush,
            FontSize = Palette.CaptionSize,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var value = new SelectableTextBlock
        {
            Text = resolved.Describe(asked),
            Foreground = resolved.IsOwn(asked) ? Palette.TextBrush : Palette.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(value, 1);
        row.Children.Add(name);
        row.Children.Add(value);
        _details.Children.Add(row);
    }

    private void OpenSelected()
    {
        if (Selected()?.Node is not ConnectionEntry entry) return;
        if (ConnectionCatalog.ToProfile(entry) is not { } profile) return;

        Chosen = profile;
        Close();
    }

    /// <summary>
    /// Copy the selection into WinMux's own profiles. The foreign file is not touched: importing is
    /// taking a copy, so the other tool goes on working exactly as it did (ADR 0025).
    /// </summary>
    private void ImportSelected()
    {
        if (Selected() is not { Node: { } node } row) return;

        var entries = node is ConnectionFolder folder ? folder.Entries().ToArray() : [(ConnectionEntry)node];
        var prefix = row.Result.Source.DisplayName.ToLowerInvariant() + "-";

        _imported.Clear();
        foreach (var entry in entries)
        {
            if (ConnectionCatalog.ToProfile(entry, prefix) is { } profile) _imported.Add(profile);
        }

        var skipped = entries.Length - _imported.Count;
        _note.Text = _imported.Count == 0
            ? "Nothing here can be opened in a pane yet, so nothing was imported."
            : $"{_imported.Count} connection(s) ready to import" +
              (skipped > 0 ? $"; {skipped} use a protocol WinMux does not open." : ".") +
              " Close this window to keep them.";
    }

    /// <summary>
    /// Move this source's stored passwords into Windows Credential Manager.
    ///
    /// Asked for rather than done on opening: reading somebody's password store is a deliberate act,
    /// and a connections window that harvested it on sight would be doing something nobody asked
    /// for. The foreign file is left exactly as it was.
    /// </summary>
    private async Task SavePasswordsAsync()
    {
        if (Selected() is not { Result: { Root: { } root } result }) return;
        if (_credentials is null) return;

        IReadOnlyList<FoundCredential> found;
        try
        {
            found = result.Source.ReadCredentials(root);
        }
        catch (ConnectionSourceException exception)
        {
            await new NoticeWindow(result.Source.DisplayName, exception.Message).ShowDialog(this);
            return;
        }

        if (found.Count == 0)
        {
            await new NoticeWindow(
                result.Source.DisplayName,
                $"{result.Source.DisplayName} has no stored passwords that WinMux can read.").ShowDialog(this);
            return;
        }

        var confirmed = await NoticeWindow.ConfirmAsync(
            this,
            $"Save {found.Count} password(s) from {result.Source.DisplayName}?",
            $"They will be copied into Windows Credential Manager, where WinMux keeps its own. " +
            $"{result.Source.Location} is not changed — {result.Source.DisplayName} goes on using it as it does now.",
            "Save them",
            "Cancel");

        if (!confirmed) return;

        var saved = 0;
        foreach (var credential in found)
        {
            // Named for where it came from, so two tools holding a password for the same server do
            // not overwrite each other and either can be found again.
            var key = $"WinMux:{result.Source.DisplayName}:{credential.Node.Path}";
            var user = ConnectionResolver.User(credential.Node).Value ?? string.Empty;

            // One that will not save is not a reason to abandon the rest; the count says how many
            // arrived, which is the honest answer either way.
            if (_credentials.TrySave(key, new StoredCredential(user, credential.Secret), out _)) saved++;
        }

        _note.Text = $"{saved} password(s) saved to Windows Credential Manager.";
    }
}
