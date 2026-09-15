using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// Ask for a user name and a password for a remote host.
///
/// Separate from <see cref="PromptWindow"/> because a password is not just a short piece of text:
/// it needs masking, it needs a second field beside it, and it needs somewhere honest to say where
/// it will be kept if the user says to keep it.
///
/// The secret goes to Windows Credential Manager and nowhere else — never to the profiles file,
/// which is plain text and gets copied between machines. See <c>ICredentialStore</c> for why that is
/// a platform contract rather than a file of our own.
/// </summary>
internal sealed class CredentialWindow : Window
{
    private readonly TextBox _user;
    private readonly TextBox _secret;
    private readonly CheckBox _remember;

    /// <summary>The user name entered, or null if the user backed out.</summary>
    public string? User { get; private set; }

    /// <summary>The password entered. Only meaningful when <see cref="User"/> is not null.</summary>
    public string Secret { get; private set; } = string.Empty;

    /// <summary>Whether to save it in the system's credential store.</summary>
    public bool Remember { get; private set; }

    /// <param name="target">What is being connected to, in words: "sftp://build.example.com".</param>
    /// <param name="user">The user name to start with, from the profile.</param>
    /// <param name="canRemember">
    /// False when there is no credential store to remember it in, which hides the option rather
    /// than offering something that would not work.
    /// </param>
    /// <param name="warning">An extra line to show in red — an unencrypted connection, say.</param>
    public CredentialWindow(string target, string user, bool canRemember, string? warning = null)
    {
        Title = "Connect";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        AppIcon.Apply(this);

        _user = new TextBox { Text = user, CornerRadius = Palette.ControlRadius };
        _secret = new TextBox
        {
            PasswordChar = '●',
            CornerRadius = Palette.ControlRadius,
        };
        _secret.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Accept();
        };

        _remember = new CheckBox
        {
            Content = "Remember this password in Windows Credential Manager",
            IsEnabled = canRemember,
            IsChecked = false,
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"Sign in to {target}.",
                    Foreground = Palette.MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
                Label("User"),
                _user,
                Label("Password"),
                _secret,
                _remember,
            },
        };

        if (warning is not null)
        {
            body.Children.Insert(1, new TextBlock
            {
                Text = warning,
                Foreground = Palette.DangerBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!canRemember)
        {
            _remember.IsChecked = false;
            ToolTip.SetTip(_remember, "This system has no credential store, so nothing can be saved.");
        }

        var ok = DialogButton("Connect", isDefault: true);
        ok.Click += (_, _) => Accept();

        var cancel = DialogButton("Cancel", isCancel: true);
        cancel.Click += (_, _) => Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = actions,
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // Straight to the password when the user name is already known, which is the common case for
        // a saved connection whose password simply is not stored yet.
        Opened += (_, _) =>
        {
            if (string.IsNullOrEmpty(_user.Text)) { _user.Focus(); _user.SelectAll(); }
            else _secret.Focus();
        };
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = Palette.CaptionSize,
        Foreground = Palette.MutedTextBrush,
    };

    private static Button DialogButton(string content, bool isDefault = false, bool isCancel = false) => new()
    {
        Content = content,
        IsDefault = isDefault,
        IsCancel = isCancel,
        // Qualified: an unqualified `Theme` binds to StyledElement.Theme, which every Window has.
        Classes = { Chrome.Theme.DialogButton },
    };

    private void Accept()
    {
        User = _user.Text?.Trim() ?? string.Empty;
        Secret = _secret.Text ?? string.Empty;
        Remember = _remember.IsChecked == true;
        Close();
    }
}
