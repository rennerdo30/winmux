using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Shell.Chrome;
using WinMux.Shell.Update;

namespace WinMux.Shell;

/// <summary>
/// The About box: what this is, which version, whose, under what licence — and whether there is a
/// newer one, with the button that installs it.
///
/// <para>
/// Every desktop application has one and WinMux did not, which left the version in a settings row
/// and updating as two items in the Help menu, "Check for updates" and "Install update…", each a
/// one-shot command that answered in the status bar. Browsers settled how this should work long
/// ago: open About, watch it check, see it download, press restart. People look for it there.
/// </para>
///
/// <para>
/// Modeless and one at a time. The state it shows belongs to <see cref="UpdateController"/>, so
/// closing the window mid-download does not stop the download, and opening it again shows where
/// it got to.
/// </para>
/// </summary>
internal sealed class AboutWindow : Window
{
    private const double ContentWidth = 520;
    private const double SidePadding = 24;

    private readonly UpdateController _updates;
    private readonly Func<bool> _automaticChecks;
    private readonly Action _restart;

    private readonly TextBlock _status = new()
    {
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock _detail = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Palette.MutedTextBrush,
        LineHeight = 19,
    };

    private readonly ProgressBar _progress = new()
    {
        Minimum = 0,
        Maximum = 1,
        Height = 4,
        MinHeight = 4,
        IsVisible = false,
    };

    private readonly Button _primary = new() { Classes = { "accent", Chrome.Theme.DialogButton } };
    private readonly Button _notes = new() { Content = "What's new", Classes = { Chrome.Theme.DialogButton } };

    /// <param name="updates">The update everything else in WinMux also drives.</param>
    /// <param name="automaticChecks">
    /// Whether checking on start is allowed. When it is off, opening this window does not go to
    /// the network by itself either — the setting promises no network access — and offers a button.
    /// </param>
    /// <param name="restart">Save the session and restart into the downloaded update.</param>
    public AboutWindow(UpdateController updates, Func<bool> automaticChecks, Action restart)
    {
        _updates = updates;
        _automaticChecks = automaticChecks;
        _restart = restart;

        Title = "About WinMux";
        Width = ContentWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        AppIcon.Apply(this);

        _primary.Click += (_, _) => OnPrimary();
        _notes.Click += (_, _) => Links.Open(_updates.Release?.Url is { Length: > 0 } url ? url : Links.Releases);

        var close = new Button { Content = "Close", IsCancel = true, Classes = { Chrome.Theme.DialogButton } };
        close.Click += (_, _) => Close();

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { close },
            },
        };

        var content = new StackPanel
        {
            Margin = new Thickness(SidePadding, 24, SidePadding, 20),
            Spacing = Palette.GapLarge,
            MaxWidth = ContentWidth - (2 * SidePadding),
            Children = { Identity(), UpdateCard(), Legal() },
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(content);
        Content = root;

        _updates.Changed += Refresh;
        Closed += (_, _) => _updates.Changed -= Refresh;

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };

        Refresh();

        // Opening About is asking. The check is skipped only when the user has turned network
        // access off, or when the answer is already on screen.
        Opened += (_, _) =>
        {
            var stale = _updates.State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.NotInstallable
                        || (_updates.State == UpdateState.Failed && _updates.Decision is null);
            if (_automaticChecks() && stale) _ = _updates.CheckAsync();
        };
    }

    /// <summary>The copyright line the assembly was built with, © rather than (c).</summary>
    internal static string Copyright =>
        (typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
         ?? "Copyright (c) 2026 rennerdo30 and WinMux contributors")
        .Replace("(c)", "©", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mark, name, version.</summary>
    private static Control Identity()
    {
        var mark = new Image
        {
            Source = AppIcon.Bitmap,
            Width = 64,
            Height = 64,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "WinMux",
                    FontSize = Palette.SubtitleSize,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Palette.TextBrush,
                    LineHeight = 26,
                },
                new SelectableTextBlock
                {
                    Text = $"Version {UpdateService.Current} (64-bit)",
                    Foreground = Palette.TextBrush,
                },
                new TextBlock
                {
                    Text = "A window multiplexer for Windows.",
                    Foreground = Palette.MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var row = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        mark.Margin = new Thickness(0, 0, Palette.GapLarge, 0);
        row.Children.Add(mark);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Where the update stands, and the one button that moves it on.</summary>
    private Control UpdateCard()
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Palette.GapSmall,
            Children = { _primary, _notes },
        };

        return new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Palette.SurfaceRadius,
            Padding = new Thickness(16, 14),
            Child = new StackPanel
            {
                Spacing = Palette.GapSmall,
                Children = { _status, _detail, _progress, actions },
            },
        };
    }

    /// <summary>Copyright, licence, notices, and where the project lives.</summary>
    private Control Legal()
    {
        var licence = LinkButton("MIT licence", () => ShowFile("LICENSE", "WinMux licence", Links.Licence));
        var notices = LinkButton("Third-party notices", () =>
            ShowFile("THIRD-PARTY-NOTICES.txt", "Third-party notices", Links.Repo));

        var links = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                LinkButton("Documentation", () => Links.Open(Links.Documentation)),
                LinkButton("Release notes", () => Links.Open(Links.Releases)),
                LinkButton("Source code", () => Links.Open(Links.Repo)),
                LinkButton("Report an issue", () => Links.Open(Links.Issues)),
            },
        };

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new SelectableTextBlock { Text = Copyright, Foreground = Palette.MutedTextBrush },
                new TextBlock
                {
                    Text = "WinMux is free software under the MIT licence, and ships with open-source " +
                           "components under their own licences.",
                    Foreground = Palette.MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19,
                },
                new WrapPanel { Children = { licence, notices } },
                links,
            },
        };
    }

    private static Button LinkButton(string text, Action click)
    {
        var button = new HyperlinkButton { Content = text, Padding = new Thickness(0, 2, 16, 2) };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// Show a text file that ships beside WinMux.exe, in a window. Opening it with the shell would
    /// hand an extension-less LICENSE to the "How do you want to open this?" dialog.
    /// </summary>
    private void ShowFile(string fileName, string title, string fallbackUrl)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A development build has neither file; the repository does.
            Links.Open(fallbackUrl);
            return;
        }

        new TextFileWindow(title, text).Show(this);
    }

    private void OnPrimary()
    {
        switch (_updates.State)
        {
            case UpdateState.Available:
                _ = _updates.DownloadAsync();
                break;
            case UpdateState.ReadyToRestart:
                _restart();
                break;
            case UpdateState.Failed:
                _ = _updates.RetryAsync();
                break;
            default:
                _ = _updates.CheckAsync();
                break;
        }
    }

    /// <summary>Redraw from the controller. Every state says what is true and what to do next.</summary>
    private void Refresh()
    {
        var release = _updates.Release;
        var (status, detail, button) = _updates.State switch
        {
            UpdateState.Checking => ("Checking for updates…", "Asking GitHub for the newest release.", null),

            UpdateState.UpToDate => (
                "WinMux is up to date",
                Checked() + Channel(),
                "Check again"),

            UpdateState.Available when release is not null => (
                $"WinMux {release.Version} is available",
                "Downloading checks the release against its published SHA-256 before anything is " +
                "installed. You will be asked to restart afterwards.",
                "Download and install"),

            UpdateState.NotInstallable when release is not null => (
                $"WinMux {release.Version} is published, but not for this machine",
                "The release has no 64-bit Windows package. Check the release notes for why.",
                "Check again"),

            UpdateState.Downloading when release is not null => (
                $"Downloading WinMux {release.Version} — {(int)(_updates.Progress * 100)}%",
                "You can close this window; the download carries on.",
                null),

            UpdateState.ReadyToRestart when release is not null => (
                $"Restart to finish updating to {release.Version}",
                "Your layout is saved and restored. Programs in panes are closed and started again, " +
                "as after any restart — they are not resumed.",
                "Restart WinMux"),

            UpdateState.Failed => (
                "The update did not complete",
                _updates.Error ?? "Something went wrong.",
                "Try again"),

            _ => _automaticChecks()
                ? ("Checking for updates…", "", null)
                : ("Automatic update checks are off",
                   "WinMux does not go to the network unless you ask. Turn checks back on in Settings → Updates.",
                   "Check for updates"),
        };

        _status.Text = status;
        _status.Foreground = _updates.State == UpdateState.Failed ? Palette.DangerBrush : Palette.TextBrush;
        _detail.Text = detail;
        _detail.IsVisible = detail.Length > 0;

        _progress.IsVisible = _updates.State == UpdateState.Downloading;
        _progress.IsIndeterminate = _updates.State == UpdateState.Downloading && _updates.Progress <= 0;
        _progress.Value = _updates.Progress;

        _primary.Content = button;
        _primary.IsVisible = button is not null;
        _notes.IsVisible = release is not null && _updates.State is not UpdateState.UpToDate;
    }

    private string Checked() =>
        _updates.LastChecked is { } at ? $"Checked at {at:HH:mm}. You have the newest release" : "You have the newest release";

    private static string Channel() =>
        Settings.ShellSettings.Current.UpdateChannel == Core.Update.UpdateChannel.Prerelease
            ? " on the prerelease channel."
            : ".";

    /// <summary>For tests: what the card says.</summary>
    internal string StatusText => _status.Text ?? string.Empty;

    /// <summary>For tests: the button's label, or null when there is none.</summary>
    internal string? PrimaryText => _primary.IsVisible ? _primary.Content as string : null;

    internal void ClickPrimary() => OnPrimary();
}
