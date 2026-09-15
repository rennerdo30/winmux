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
internal sealed record EmptyPaneCommands(
    Action<PaneId, LaunchProfile> OpenProfile,
    Action<PaneId> ChooseApplication,
    Action<PaneId> AttachWindow);

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
    Func<IReadOnlyList<LaunchProfile>> profiles) : IPaneProvider
{
    public PaneKind Kind => PaneKind.Empty;

    public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
        ValueTask.FromResult<IPaneRuntime>(new EmptyPaneRuntime(context, commands, profiles));

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
        Func<IReadOnlyList<LaunchProfile>> profiles)
    {
        _pane = new Pane(context.PaneId, PaneKind.Empty, context.Title, context.Descriptor);

        var heading = new TextBlock
        {
            Text = "Empty pane",
            FontSize = Palette.SubtitleSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TextBrush,
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
            Margin = new Thickness(0, 2, 0, 10),
        };

        var buttons = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var profile in profiles())
        {
            buttons.Children.Add(Action(
                profile.Name,
                profile.Kind == ProfileKind.Terminal ? "terminal" : "application",
                () => commands.OpenProfile(_pane.Id, profile)));
        }

        var extras = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        extras.Children.Add(Secondary("Choose an application…", () => commands.ChooseApplication(_pane.Id)));
        extras.Children.Add(Secondary("Attach an open window…", () => commands.AttachWindow(_pane.Id)));

        var content = new StackPanel
        {
            Margin = new Thickness(22),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { heading, _note, buttons, extras },
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

    private static Button Action(string label, string kind, Action invoke)
    {
        var button = new Button
        {
            Padding = new Thickness(12, 7),
            CornerRadius = Palette.ControlRadius,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 280,
            Content = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new TextBlock
                    {
                        Text = kind,
                        FontSize = Palette.CaptionSize,
                        Foreground = Palette.FaintTextBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        [DockPanel.DockProperty] = Dock.Right,
                    },
                    new TextBlock { Text = label, Foreground = Palette.TextBrush },
                },
            },
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
        };
        button.Click += (_, _) => invoke();
        return button;
    }
}
