using System.Diagnostics;
using WinMux.Shell.Update;

namespace WinMux.Shell.Tests;

/// <summary>
/// The update's replacement script, run for real by PowerShell against a pretend installation.
///
/// It is the one piece of the updater that runs after WinMux has gone, hidden, with nobody to see it
/// fail — which is exactly how the first version failed for a user: it renamed the installation
/// directory, a handle inside it made that impossible, and WinMux never came back. So it is tested
/// end to end, including a file held open the way a running terminal host holds its image.
/// </summary>
public sealed class UpdateScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winmux-update-").FullName;
    private string Current => Path.Combine(_root, "WinMux");
    private string Staged => Path.Combine(_root, "staged");
    private string Log => Path.Combine(_root, "update.log");
    private string Result => Path.Combine(_root, "update-result.txt");

    public UpdateScriptTests()
    {
        Write(Current, "WinMux.exe", "old app");
        Write(Current, "WinMux.dll", "old library");
        Write(Current, "arm64/OpenConsole.exe", "old console host");
        Write(Current, "foreign-app-quirks.json", "{ \"edited\": \"by the user\" }");
        Write(Current, "only-in-old.txt", "left alone");

        Write(Staged, "WinMux.exe", "new app");
        Write(Staged, "WinMux.dll", "new library");
        Write(Staged, "arm64/OpenConsole.exe", "new console host");
        Write(Staged, "foreign-app-quirks.json", "{ \"shipped\": true }");
        Write(Staged, "only-in-new.dll", "new file");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Files_in_use_are_replaced_and_the_outcome_is_recorded()
    {
        // Opened the way a running image is: others may read it and rename it, not overwrite it.
        using (var running = new FileStream(
                   Path.Combine(Current, "arm64", "OpenConsole.exe"),
                   FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Run(attempts: 40);
        }

        Assert.Equal("new app", Read("WinMux.exe"));
        Assert.Equal("new library", Read("WinMux.dll"));
        Assert.Equal("new console host", Read("arm64/OpenConsole.exe"));
        Assert.Equal("new file", Read("only-in-new.dll"));
        Assert.Equal("left alone", Read("only-in-old.txt"));
        Assert.Equal("old console host", Read("arm64/OpenConsole.exe.winmux-old"));

        // The user's quirks are kept beside the shipped ones, not lost to them.
        Assert.Equal("{ \"edited\": \"by the user\" }", Read("foreign-app-quirks.json.before-update"));

        Assert.Equal("ok|0.9.9", File.ReadAllText(Result).Trim());
        Assert.False(Directory.Exists(Staged), "a finished update clears its staging directory");
        Assert.Contains("installed", File.ReadAllText(Log));
    }

    [Fact]
    public void A_file_that_cannot_be_moved_rolls_everything_back()
    {
        // Locked outright: not even a rename is allowed.
        using (var locked = new FileStream(
                   Path.Combine(Current, "WinMux.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Run(attempts: 2);
        }

        Assert.Equal("old app", Read("WinMux.exe"));
        Assert.Equal("old library", Read("WinMux.dll"));
        Assert.Equal("old console host", Read("arm64/OpenConsole.exe"));
        Assert.False(File.Exists(Path.Combine(Current, "only-in-new.dll")), "a file the update added is removed again");
        Assert.Empty(Directory.EnumerateFiles(Current, "*.winmux-old", SearchOption.AllDirectories));

        Assert.StartsWith("failed|", File.ReadAllText(Result).Trim());
        Assert.Contains("rolling back", File.ReadAllText(Log));
        Assert.True(Directory.Exists(Staged), "a failed update keeps what it downloaded");
    }

    [Fact]
    public void The_downloaded_application_must_be_the_windowed_one()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(UpdateInstaller.IsWindowsApplication(Path.Combine(windows, "notepad.exe")));
        Assert.False(UpdateInstaller.IsWindowsApplication(Path.Combine(windows, "System32", "cmd.exe")));
        Assert.False(UpdateInstaller.IsWindowsApplication(Path.Combine(Current, "WinMux.exe")), "a text file is not a program");
    }

    private void Run(int attempts)
    {
        // A process id that has certainly exited, so the script does not wait.
        using var finished = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        finished.WaitForExit();

        var script = Path.Combine(_root, "update.ps1");
        File.WriteAllText(script, UpdateInstaller.BuildScript(
            finished.Id, Current, Staged, Path.Combine(_root, "session.toml"), "0.9.9", Log, Result,
            relaunch: false, attempts: attempts));

        using var powershell = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        })!;

        var errors = powershell.StandardError.ReadToEnd();
        Assert.True(powershell.WaitForExit(60_000), "the script finished");
        Assert.True(File.Exists(Result), $"the script recorded an outcome. stderr: {errors}");
    }

    private static void Write(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(Current, relative));
}
