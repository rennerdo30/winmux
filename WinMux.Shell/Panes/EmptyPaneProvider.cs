using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Panes;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Panes;

/// <summary>What an empty pane can ask the shell to put in it.</summary>
/// <param name="OpenProfile">Replace this pane with a profile's pane.</param>
/// <param name="ChooseApplication">Pick an application from the catalogue, then open it here.</param>
/// <param name="AttachWindow">Pick a window that is already open, then take it into this pane.</param>
/// <param name="OpenFileBrowser">Put WinMux's own file browser here.</param>
/// <param name="OpenPaneKind">Put a registered pane kind here — anything a provider supplies.</param>
/// <param name="Connect">Set up a connection of this kind, then open it here.</param>
internal sealed record EmptyPaneCommands(
    Action<PaneId, LaunchProfile> OpenProfile,
    Action<PaneId> ChooseApplication,
    Action<PaneId> AttachWindow,
    Action<PaneId> OpenFileBrowser,
    Action<PaneId, PaneKind> OpenPaneKind,
    Action<PaneId, ProfileKind> Connect,
    Action<PaneId> MakeTabGroup);

/// <summary>
/// A pane with nothing in it, offering everything that could go in it.
///
/// This is why splitting can be separated from deciding: <c>Ctrl+B %</c> gives you a pane, and the
/// pane itself asks what it should be. The same list appears here, in the toolbar's New menu and in
/// the palette, because all three read the profile list (CLAUDE.md section 5a).
///
/// It is also the honest landing place for an adopted window that could not be restored — the
/// window is gone, but the pane and the layout around it are not.
/// </summary>
internal sealed class EmptyPaneProvider(
    EmptyPaneCommands commands,
    Func<IReadOnlyList<LaunchProfile>> profiles,
    WinMux.Platform.IAppIconSource icons,
    Func<IReadOnlyList<(PaneKind Kind, string Name)>> paneKinds) : IPaneProvider
{
    public PaneKind Kind => PaneKind.Empty;

    /// <summary>An empty pane cannot offer to become another empty pane.</summary>
    public bool IsOfferedDirectly => false;

    public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
        ValueTask.FromResult<IPaneRuntime>(new EmptyPaneRuntime(context, commands, profiles, icons, paneKinds));

    public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
        CreateAsync(context, token);
}

internal sealed class EmptyPaneRuntime : IPaneRuntime
{
    private readonly Pane _pane;
    private readonly TextBlock _note;

    public EmptyPaneRuntime(
        PaneProviderContext context,
        EmptyPaneCommands commands,
        Func<IReadOnlyList<LaunchProfile>> profiles,
        WinMux.Platform.IAppIconSource icons,
        Func<IReadOnlyList<(PaneKind Kind, string Name)>> paneKinds)
    {
        _pane = new Pane(context.PaneId, PaneKind.Empty, context.Title, context.Descriptor);

        var heading = new TextBlock
        {
            Text = "Empty pane",
            FontSize = Palette.SubtitleSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TextBrush,

            // Without an explicit line height the ascenders were clipped: the measured height came
            // out shorter than the glyphs, and the top of "Empty pane" was sliced off.
            LineHeight = Palette.SubtitleSize * 1.4,
        };

        // A restored pane may carry the reason its previous contents are missing. Saying so beats
        // an empty pane that looks like the user made it by accident.
        _note = new TextBlock
        {
            Text = context.Descriptor.Extras.TryGetValue("note", out var note) && note.Length > 0
                ? note
                : "Choose what to open here.",
            Foreground = Palette.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, Palette.GapMedium),
        };

        var list = new StackPanel { Spacing = 2 };

        // Grouped by what the thing actually is. One undifferentiated column of grey slabs made a
        // shell, an application and a saved server look like the same kind of choice.
        foreach (var group in profiles()
            .GroupBy(profile => Section(profile.Kind))
            .OrderBy(group => SectionOrder(group.Key)))
        {
            list.Children.Add(SectionHeading(group.Key));
            foreach (var profile in group)
            {
                var entry = profile;
                list.Children.Add(Entry(
                    entry.Name,
                    Describe(entry.Kind),
                    IconFor(entry, icons),
                    () => commands.OpenProfile(_pane.Id, entry)));
            }
        }

        // Every registered pane kind that offers itself, not a hardcoded one. ADR 0012 says adding a
        // pane kind must not touch Core, the tree, persistence or the CLI; it should not have to
        // touch this screen either, and an external provider nobody can choose is the section 5a
        // failure in its purest form -- the sample clock provider shipped and was unreachable here.
        var kinds = paneKinds();
        if (kinds.Count > 0)
        {
            list.Children.Add(SectionHeading("Built in"));
            foreach (var (kind, name) in kinds)
            {
                var target = kind;
                // Only show the identifier when it is not just the name again — a provider that
                // names itself "Clock" does not need "Clock" written underneath it.
                var detail = kind == PaneKind.FileBrowser
                    ? "browse files in this pane"
                    : string.Equals(name, kind.Value, StringComparison.OrdinalIgnoreCase)
                        ? "provider pane"
                        : kind.Value;

                list.Children.Add(Entry(
                    name,
                    detail,
                    Icons.Folder(),
                    () => commands.OpenPaneKind(_pane.Id, target)));
            }
        }

        // One row per protocol rather than a single "Connect to a server…". That phrasing named
        // nothing: it gave no way to know that Remote Desktop was behind it, which is the same
        // failure as hiding the capability entirely — you cannot choose what you cannot see.
        list.Children.Add(SectionHeading("Connect to"));
        foreach (var (kind, name, detail, icon) in ConnectionChoices())
        {
            var target = kind;
            list.Children.Add(Entry(name, detail, icon, () => commands.Connect(_pane.Id, target)));
        }

        // Wraps rather than laying out in one row: two buttons of this width exceed the column, and
        // the second was simply cut off at the edge.
        var extras = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, Palette.GapMedium, 0, 0),
        };
        extras.Children.Add(Secondary("Choose an application…", () => commands.ChooseApplication(_pane.Id)));
        extras.Children.Add(Secondary("Attach an open window…", () => commands.AttachWindow(_pane.Id)));

        // Not everything you can do with an empty pane is a decision about what goes *in* it. Making
        // it a tab group is a decision about the shape around it, and it was reachable only from the
        // toolbar — which is a long way to go from the pane that is asking the question.
        extras.Children.Add(Secondary("Make this a tab group", () => commands.MakeTabGroup(_pane.Id)));

        var content = new StackPanel
        {
            // A fixed column rather than shrink-to-fit: the rows line up with each other and with
            // the heading, instead of every row being as wide as its own longest word.
            Width = 420,
            Margin = new Thickness(Palette.GapLarge),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { heading, _note, list, extras },
        };

        View = new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Palette.SurfaceRadius,
            Child = new ScrollViewer { Content = content },
        };
    }

    /// <summary>
    /// The connections that can be set up from here, each named as the thing it is.
    ///
    /// Descriptions say what you get, not what the protocol is called: somebody who wants "that
    /// machine's desktop" should not have to know that the answer is spelled RDP.
    /// </summary>
    private static IEnumerable<(ProfileKind Kind, string Name, string Detail, Control Icon)> ConnectionChoices()
    {
        yield return (ProfileKind.Ssh, "SSH", "a terminal on another machine", Icons.Terminal());
        yield return (ProfileKind.Rdp, "Remote Desktop", "that machine's desktop, in this pane", Icons.TabGroup());
        yield return (ProfileKind.Sftp, "SFTP", "browse another machine's files over SSH", Icons.Folder());
        yield return (ProfileKind.Ftp, "FTP", "browse another machine's files over FTP", Icons.Folder());
    }

    private static string Section(ProfileKind kind) => kind switch
    {
        ProfileKind.Terminal => "Terminals",
        ProfileKind.Ssh or ProfileKind.Rdp or ProfileKind.Sftp or ProfileKind.Ftp => "Connections",
        _ => "Applications",
    };

    private static int SectionOrder(string section) => section switch
    {
        "Terminals" => 0,
        "Applications" => 1,
        _ => 2,
    };

    private static TextBlock SectionHeading(string text) => new()
    {
        Text = text,
        FontSize = Palette.CaptionSize,
        FontWeight = FontWeight.SemiBold,
        Foreground = Palette.MutedTextBrush,
        Margin = new Thickness(2, Palette.GapMedium, 0, 4),
    };

    /// <summary>
    /// The application's own icon where there is one, and a drawn glyph otherwise.
    ///
    /// Terminals have an executable too — cmd.exe and powershell.exe both carry icons — so they get
    /// the real thing rather than a generic mark, which is what makes the list scannable at a
    /// glance instead of a column of identical rectangles.
    /// </summary>
    private static Control IconFor(LaunchProfile profile, WinMux.Platform.IAppIconSource icons)
    {
        if (profile.Kind is ProfileKind.Sftp or ProfileKind.Ftp) return Icons.Folder();

        if (profile.Program.Length > 0 && icons.IsAvailable &&
            icons.GetIconPng(profile.Program, 32) is { } png)
        {
            try
            {
                using var stream = new MemoryStream(png);
                return new Image
                {
                    Source = new Avalonia.Media.Imaging.Bitmap(stream),
                    Width = IconPixels,
                    Height = IconPixels,
                };
            }
            catch (Exception)
            {
                // A picture that will not decode is not worth a message; fall through to the glyph.
            }
        }

        return profile.Kind == ProfileKind.Terminal ? Icons.Terminal() : Icons.Folder();
    }

    public PaneId PaneId => _pane.Id;
    public PaneKind Kind => PaneKind.Empty;
    public Control View { get; }
    public string? StatusMessage => null;
    /// <summary>
    /// Never raised. An empty pane has no state that changes: it shows a launcher until something
    /// replaces it, and the replacement is a different runtime entirely.
    /// </summary>
    public event EventHandler? StateChanged { add { } remove { } }

    public bool Focus() => View.Focus();
    public void Arrange(PaneArrangement arrangement) { }
    public void RefreshRestoreState() { }
    public RestoreDescriptor CaptureRestoreDescriptor() => _pane.Restore;

    public ValueTask<PaneCloseResult> CloseAsync(PaneCloseReason reason, CancellationToken token = default) =>
        ValueTask.FromResult(PaneCloseResult.Success("empty pane closed"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// What a profile opens, in the words the user would use.
    ///
    /// This said "terminal" or "application" and nothing else, so an SFTP connection was labelled an
    /// application — which is both wrong and unhelpful, since the thing it opens is a file browser.
    /// </summary>
    private static string Describe(ProfileKind kind) => kind switch
    {
        ProfileKind.Terminal => "terminal",
        ProfileKind.Ssh => "SSH",
        ProfileKind.Rdp => "Remote Desktop",
        ProfileKind.Sftp => "SFTP",
        ProfileKind.Ftp => "FTP",
        _ => "application",
    };

    private const int IconPixels = 20;

    /// <summary>
    /// One choice: its icon, its name, and underneath in small type what it actually is.
    ///
    /// The name and the kind used to sit on one line with the kind docked right — and ran into each
    /// other, reading "Command Prompt<em>terminal</em>". The cause is the trap CLAUDE.md records
    /// about star columns, in a different costume: <c>HorizontalContentAlignment.Left</c> makes the
    /// content take its *desired* width rather than the button's, so "dock to the right edge" put it
    /// against the end of the text instead of the end of the row. Stretching the content fixes it,
    /// and putting the kind on its own line makes the name the thing you read first.
    /// </summary>
    private static Button Entry(string label, string kind, Control icon, Action invoke)
    {
        icon.Width = IconPixels;
        icon.Height = IconPixels;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 10, 0);

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = label, Foreground = Palette.TextBrush },
                new TextBlock
                {
                    Text = kind,
                    FontSize = Palette.CaptionSize,
                    Foreground = Palette.FaintTextBrush,
                },
            },
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var button = new Button
        {
            Padding = new Thickness(10, 7),
            CornerRadius = Palette.ControlRadius,
            HorizontalAlignment = HorizontalAlignment.Stretch,

            // Stretch, not Left. See the note above: Left shrink-wraps the content and the layout
            // inside it stops meaning what it says.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = row,
        };

        button.Click += (_, _) => invoke();
        return button;
    }

    private static Button Secondary(string label, Action invoke)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 7),
            CornerRadius = Palette.ControlRadius,

            // The spacing lives here rather than on the panel: a WrapPanel has no Spacing, and the
            // gap has to survive a row break as well as sit between neighbours.
            Margin = new Thickness(0, 0, Palette.GapSmall, Palette.GapSmall),
        };
        button.Click += (_, _) => invoke();
        return button;
    }
}
