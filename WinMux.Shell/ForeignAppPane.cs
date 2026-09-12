using System.Diagnostics;
using WinMux.Core.Model;

namespace WinMux.Shell;

/// <summary>
/// Owns one out-of-process pane host. The shell only positions the host's top-level window;
/// every synchronous operation on the foreign application happens inside WinMux.PaneHost.
/// </summary>
internal sealed class ForeignAppPane
{
    private Process? _hostProcess;

    public PaneId Id { get; }
    public Pane Pane { get; }
    public IntPtr Hwnd { get; private set; }
    public IntPtr ChildHwnd { get; private set; }
    public string Status { get; private set; } = "starting…";
    public bool Located => Hwnd != IntPtr.Zero && Win32Interop.IsWindow(Hwnd);

    public ForeignAppPane(Pane pane)
    {
        Pane = pane;
        Id = pane.Id;
    }

    public async Task LaunchAsync(ISet<IntPtr> claimed, IntPtr ownerWindow, CancellationToken token)
    {
        var restore = Pane.Restore;
        if (string.IsNullOrWhiteSpace(restore.Program))
        {
            Status = "no program set";
            return;
        }

        var hostExecutable = Path.Combine(AppContext.BaseDirectory, "WinMux.PaneHost.exe");
        if (!File.Exists(hostExecutable))
        {
            Status = "WinMux.PaneHost.exe is missing; rebuild WinMux.Shell";
            return;
        }

        var start = new ProcessStartInfo(hostExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--program");
        start.ArgumentList.Add(restore.Program);
        if (ownerWindow != IntPtr.Zero)
        {
            start.ArgumentList.Add("--owner");
            start.ArgumentList.Add(ownerWindow.ToInt64().ToString());
        }
        if (restore.Extras.GetValueOrDefault("window_class") is { Length: > 0 } windowClass)
        {
            start.ArgumentList.Add("--window-class");
            start.ArgumentList.Add(windowClass);
        }
        lock (claimed)
        {
            foreach (var hwnd in claimed)
            {
                start.ArgumentList.Add("--exclude");
                start.ArgumentList.Add(hwnd.ToInt64().ToString());
            }
        }
        start.ArgumentList.Add("--");
        foreach (var argument in restore.Args) start.ArgumentList.Add(argument);

        if (restore.Cwd.IsKnown && Directory.Exists(restore.Cwd.Path))
            start.WorkingDirectory = restore.Cwd.Path;
        foreach (var (name, value) in restore.EnvOverrides) start.Environment[name] = value;

        try
        {
            _hostProcess = Process.Start(start);
            if (_hostProcess is null) throw new InvalidOperationException("process did not start");
        }
        catch (Exception ex)
        {
            Status = "could not start pane host: " + ex.Message;
            return;
        }

        Status = "waiting for pane host…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            while (await _hostProcess.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (TryReadHandle(line, "HOST_HWND=", out var host))
                {
                    Hwnd = host;
                    Status = "waiting for application window…";
                    continue;
                }
                if (TryReadHandle(line, "READY=", out var child))
                {
                    ChildHwnd = child;
                    lock (claimed) claimed.Add(child);
                    Status = "hosted out of process";
                    return;
                }
                if (line.StartsWith("ERROR=", StringComparison.Ordinal))
                {
                    Status = line[6..];
                    Detach();
                    Hwnd = IntPtr.Zero;
                    return;
                }
            }

            var error = await _hostProcess.StandardError.ReadToEndAsync(timeout.Token);
            Status = string.IsNullOrWhiteSpace(error) ? "pane host exited before it was ready" : error.Trim();
        }
        catch (OperationCanceledException)
        {
            Status = token.IsCancellationRequested ? "cancelled" : "pane host did not become ready within 20 seconds";
            Detach();
        }
    }

    private static bool TryReadHandle(string line, string prefix, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
               long.TryParse(line.AsSpan(prefix.Length), out var value) &&
               (hwnd = new IntPtr(value)) != IntPtr.Zero;
    }

    /// <summary>Detach the app and let it survive shell shutdown.</summary>
    public void Detach()
    {
        SendCommand("DETACH");
        Hwnd = IntPtr.Zero;
        ChildHwnd = IntPtr.Zero;
    }

    /// <summary>Close the app because the user explicitly closed its pane.</summary>
    public void Close()
    {
        SendCommand("CLOSE");
        Hwnd = IntPtr.Zero;
        ChildHwnd = IntPtr.Zero;
    }

    private void SendCommand(string command)
    {
        try
        {
            if (_hostProcess is { HasExited: false })
            {
                _hostProcess.StandardInput.WriteLine(command);
                _hostProcess.StandardInput.Flush();
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }
}
