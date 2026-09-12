using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WinMux.Shell.Cwd;

namespace WinMux.Shell;

/// <summary>A visible, reversible setup surface for the cwd reporters required by ADR 0004.</summary>
internal sealed class CwdIntegrationWindow : Window
{
    private readonly ProfileInstaller _installer;
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly Button _install = new() { Content = "Install Windows integrations", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };

    public CwdIntegrationWindow(ProfileInstaller installer, ProfileInstallationReport report)
    {
        _installer = installer;
        Title = "WinMux working-directory setup";
        Width = 680;
        Height = 520;
        MinWidth = 520;
        MinHeight = 360;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));

        var explanation = new TextBlock
        {
            Text = "WinMux can only restore PowerShell and WSL to their latest directory when the shell reports it. " +
                   "The installer appends a marked import and backs up existing Windows profiles; WSL remains a manual step.",
            TextWrapping = TextWrapping.Wrap,
        };
        _install.Click += (_, _) => Refresh(_installer.Install());
        var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var panel = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children = { explanation, _install, _results, close },
        };
        Content = new ScrollViewer { Content = panel };
        Refresh(report);
    }

    private void Refresh(ProfileInstallationReport report)
    {
        _results.Children.Clear();
        foreach (var status in report.Shells)
        {
            var state = status.State switch
            {
                ProfileInstallState.Installed or ProfileInstallState.AlreadyInstalled => "ready",
                ProfileInstallState.ManualActionRequired => "manual step",
                ProfileInstallState.Failed => "failed",
                _ => "not installed",
            };
            _results.Children.Add(new TextBlock
            {
                Text = $"{DisplayName(status.Shell)} — {state}\n{status.Message}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        _install.IsEnabled = report.Shells.Any(status =>
            status.Shell != CwdProfileShell.WslBash &&
            status.State is ProfileInstallState.NotInstalled or ProfileInstallState.Failed);
    }

    private static string DisplayName(CwdProfileShell shell) => shell switch
    {
        CwdProfileShell.WindowsPowerShell => "Windows PowerShell 5.1",
        CwdProfileShell.PowerShell => "PowerShell 7+",
        CwdProfileShell.CommandPrompt => "Command Prompt",
        CwdProfileShell.WslBash => "WSL bash/zsh",
        _ => shell.ToString(),
    };
}

/// <summary>Records that each missing shell's warning has been shown; the palette can reopen it.</summary>
internal static class CwdIntegrationOnboarding
{
    public static string StatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinMux",
        "cwd-onboarding-v1.txt");

    public static IReadOnlyList<CwdProfileShell> Unseen(ProfileInstallationReport report)
    {
        HashSet<string> seen;
        try
        {
            seen = File.Exists(StatePath)
                ? File.ReadAllLines(StatePath).ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            seen = [];
        }

        return report.Shells
            .Where(status => status.State is not ProfileInstallState.Installed and not ProfileInstallState.AlreadyInstalled)
            .Select(status => status.Shell)
            .Where(shell => !seen.Contains(shell.ToString()))
            .Distinct()
            .ToArray();
    }

    public static void MarkSeen(IEnumerable<CwdProfileShell> shells)
    {
        try
        {
            var all = File.Exists(StatePath)
                ? File.ReadAllLines(StatePath).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var shell in shells) all.Add(shell.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllLines(StatePath, all.Order(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The warning was still visible. Failure to remember dismissal must not block WinMux.
        }
    }
}
