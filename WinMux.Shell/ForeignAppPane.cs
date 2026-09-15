using System.Diagnostics;
using WinMux.Core.Model;
using WinMux.Platform;
using WinMux.Platform.Win32.ForeignApps;

namespace WinMux.Shell;

/// <summary>
/// Owns one out-of-process pane host. The shell only positions the host's top-level window;
/// every synchronous operation on the foreign application happens inside WinMux.PaneHost.
/// </summary>
internal sealed class ForeignAppPane
{
    private Process? _hostProcess;
    private readonly SemaphoreSlim _protocolLock = new(1, 1);
    private readonly IHostWindowService _windows;

    public PaneId Id { get; }
    public Pane Pane { get; }
    public WindowHandle Hwnd { get; private set; }
    public WindowHandle ChildHwnd { get; private set; }
    public HostStrategy? EffectiveStrategy { get; private set; }
    public string Status { get; private set; } = "starting…";
    public string? Notice { get; private set; }
    public bool Located => _windows.IsAlive(Hwnd);

    public ForeignAppPane(Pane pane, IHostWindowService windows)
    {
        Pane = pane;
        Id = pane.Id;
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public async Task LaunchAsync(ISet<WindowHandle> claimed, WindowHandle ownerWindow, CancellationToken token)
    {
        var restore = Pane.Restore;
        if (string.IsNullOrWhiteSpace(restore.Program))
        {
            Status = "no program set";
            return;
        }

        ForeignAppLaunchPlan plan;
        var quirksPath = Path.Combine(AppContext.BaseDirectory, "foreign-app-quirks.json");
        try
        {
            plan = ForeignAppLaunchPlan.Resolve(restore, ForeignAppQuirksDatabase.Load(quirksPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ForeignAppQuirksFormatException or ArgumentException)
        {
            Status = $"cannot load foreign-app quirks from {quirksPath}: {ex.Message}";
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
        // An adopted pane names a window that already exists, so PaneHost must not launch
        // anything. The handle is not persisted as a handle — see CaptureRestoreDescriptor.
        var adopting = restore.Extras.TryGetValue("adopt_window", out var adoptText) &&
                       long.TryParse(adoptText, out var adoptValue) && adoptValue != 0;
        if (adopting)
        {
            start.ArgumentList.Add("--adopt");
            start.ArgumentList.Add(adoptText!);
        }

        start.ArgumentList.Add("--program");
        start.ArgumentList.Add(restore.Program);
        start.ArgumentList.Add("--strategy");
        start.ArgumentList.Add(plan.StrategyArgument);
        start.ArgumentList.Add("--settle-ms");
        start.ArgumentList.Add(plan.SettleMilliseconds.ToString());
        start.ArgumentList.Add("--match-mode");
        start.ArgumentList.Add(plan.MatchModeArgument);
        if (!ownerWindow.IsNone)
        {
            start.ArgumentList.Add("--owner");
            start.ArgumentList.Add(ownerWindow.Value.ToString());
        }
        if (plan.WindowClass is { Length: > 0 } windowClass)
        {
            start.ArgumentList.Add("--window-class");
            start.ArgumentList.Add(windowClass);
        }
        if (plan.TitleContains is { Length: > 0 } titleContains)
        {
            start.ArgumentList.Add("--window-title-contains");
            start.ArgumentList.Add(titleContains);
        }
        if (plan.ProcessName is { Length: > 0 } processName)
        {
            start.ArgumentList.Add("--process-name");
            start.ArgumentList.Add(processName);
        }
        lock (claimed)
        {
            foreach (var hwnd in claimed)
            {
                start.ArgumentList.Add("--exclude");
                start.ArgumentList.Add(hwnd.Value.ToString());
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
        var startupTimeout = TimeSpan.FromMilliseconds(Math.Max(20_000L, plan.SettleMilliseconds + 15_000L));
        timeout.CancelAfter(startupTimeout);

        await _protocolLock.WaitAsync(token);
        try
        {
            try
            {
                var reportedStrategy = plan.Strategy;
                while (await _hostProcess.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (TryReadHandle(line, "HOST_HWND=", out var host))
                    {
                        Hwnd = host;
                        Status = "waiting for application window…";
                        continue;
                    }
                    if (line.StartsWith("STRATEGY=", StringComparison.OrdinalIgnoreCase))
                    {
                        reportedStrategy = line["STRATEGY=".Length..].Equals("attach", StringComparison.OrdinalIgnoreCase)
                            ? HostStrategy.Attach
                            : HostStrategy.Embed;
                        continue;
                    }
                    if (line.StartsWith("NOTICE=", StringComparison.Ordinal))
                    {
                        Notice = line[7..];
                        Status = Notice;
                        continue;
                    }
                    if (TryReadReady(line, out var child, out var effectiveStrategy))
                    {
                        if (!line.Contains("strategy=", StringComparison.OrdinalIgnoreCase))
                            effectiveStrategy = reportedStrategy;
                        ChildHwnd = child;
                        EffectiveStrategy = effectiveStrategy;
                        lock (claimed) claimed.Add(child);
                        var mode = effectiveStrategy == HostStrategy.Attach ? "attached" : "embedded";
                        var detail = Notice ?? plan.Limitation;
                        Status = detail is { Length: > 0 }
                            ? $"{mode} out of process — {detail}"
                            : $"{mode} out of process";

                        // A failed explicit/automatic embed that safely fell back becomes an explicit
                        // per-app override in the session. A measured auto→attach choice stays Auto so
                        // future quirks updates can still improve it.
                        if (effectiveStrategy == HostStrategy.Attach && plan.Strategy == HostStrategy.Embed)
                            Pane.Restore = Pane.Restore with { Strategy = HostStrategy.Attach };
                        return;
                    }
                    if (line.StartsWith("ERROR=", StringComparison.Ordinal))
                    {
                        Status = line[6..];
                        SendCommand("DETACH");
                        Hwnd = WindowHandle.None;
                        return;
                    }
                }

                var error = await _hostProcess.StandardError.ReadToEndAsync(timeout.Token);
                Status = string.IsNullOrWhiteSpace(error) ? "pane host exited before it was ready" : error.Trim();
            }
            catch (OperationCanceledException)
            {
                Status = token.IsCancellationRequested
                    ? "cancelled"
                    : $"pane host did not become ready within {startupTimeout.TotalSeconds:0.#} seconds";
                SendCommand("DETACH");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Status = "pane host protocol failed: " + ex.Message;
                SendCommand("DETACH");
            }
        }
        finally
        {
            _protocolLock.Release();
        }
    }

    private static bool TryReadHandle(string line, string prefix, out WindowHandle hwnd)
    {
        hwnd = WindowHandle.None;
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
               long.TryParse(line.AsSpan(prefix.Length), out var value) &&
               !(hwnd = WindowHandle.FromPlatformValue((nint)value)).IsNone;
    }

    internal static bool TryReadReady(string line, out WindowHandle hwnd, out HostStrategy strategy)
    {
        hwnd = WindowHandle.None;
        strategy = HostStrategy.Embed;
        if (!line.StartsWith("READY=", StringComparison.Ordinal)) return false;

        var fields = line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!long.TryParse(fields[0].AsSpan("READY=".Length), out var value) || value == 0) return false;
        hwnd = WindowHandle.FromPlatformValue((nint)value);

        foreach (var field in fields.Skip(1))
        {
            if (!field.StartsWith("strategy=", StringComparison.OrdinalIgnoreCase)) continue;
            strategy = field["strategy=".Length..].ToLowerInvariant() switch
            {
                "embed" => HostStrategy.Embed,
                "attach" => HostStrategy.Attach,
                _ => strategy,
            };
        }
        return true;
    }

    public async Task<ForeignAppSwitchResult> SwitchStrategyAsync(
        HostStrategy target,
        CancellationToken token = default)
    {
        if (target == HostStrategy.Auto)
            return new ForeignAppSwitchResult(false, EffectiveStrategy, "auto must resolve before switching");
        if (_hostProcess is null || _hostProcess.HasExited || ChildHwnd.IsNone)
            return new ForeignAppSwitchResult(false, EffectiveStrategy, "the foreign application is not ready");

        await _protocolLock.WaitAsync(token);
        try
        {
            Notice = null;
            Status = $"switching to {target.ToString().ToLowerInvariant()}…";
            if (!SendCommand("STRATEGY=" + target.ToString().ToUpperInvariant()))
                return new ForeignAppSwitchResult(false, EffectiveStrategy, "could not contact PaneHost");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var reported = target;
            while (await _hostProcess.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("NOTICE=", StringComparison.Ordinal))
                {
                    Notice = line["NOTICE=".Length..];
                    continue;
                }
                if (line.StartsWith("STRATEGY=", StringComparison.OrdinalIgnoreCase))
                {
                    reported = line["STRATEGY=".Length..].Equals("attach", StringComparison.OrdinalIgnoreCase)
                        ? HostStrategy.Attach
                        : HostStrategy.Embed;
                    continue;
                }
                if (TryReadReady(line, out var child, out var effective))
                {
                    if (!line.Contains("strategy=", StringComparison.OrdinalIgnoreCase)) effective = reported;
                    ChildHwnd = child;
                    EffectiveStrategy = effective;
                    Pane.Restore = Pane.Restore with { Strategy = effective };
                    var mode = effective == HostStrategy.Attach ? "attached" : "embedded";
                    Status = Notice is { Length: > 0 }
                        ? $"{mode} out of process — {Notice}"
                        : $"{mode} out of process";
                    var reachedTarget = effective == target;
                    return new ForeignAppSwitchResult(
                        reachedTarget,
                        effective,
                        reachedTarget ? $"switched to {mode}" : Notice ?? $"remained in {mode} mode");
                }
                if (line.StartsWith("ERROR=", StringComparison.Ordinal))
                {
                    Status = line["ERROR=".Length..];
                    return new ForeignAppSwitchResult(false, EffectiveStrategy, Status);
                }
            }

            Status = "PaneHost exited while changing strategy";
            return new ForeignAppSwitchResult(false, EffectiveStrategy, Status);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Status = "PaneHost did not change strategy within 10 seconds";
            return new ForeignAppSwitchResult(false, EffectiveStrategy, Status);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Status = "PaneHost strategy switch failed: " + ex.Message;
            return new ForeignAppSwitchResult(false, EffectiveStrategy, Status);
        }
        finally
        {
            _protocolLock.Release();
        }
    }

    /// <summary>Detach the app and let it survive shell shutdown.</summary>
    public Task<ForeignAppShutdownResult> DetachAsync(CancellationToken token = default) =>
        ShutdownAsync("DETACH", "DETACHED", token);

    /// <summary>Close the app because the user explicitly closed its pane.</summary>
    public Task<ForeignAppShutdownResult> CloseAsync(CancellationToken token = default) =>
        ShutdownAsync("CLOSE", "CLOSED", token);

    private async Task<ForeignAppShutdownResult> ShutdownAsync(
        string command,
        string acknowledgement,
        CancellationToken token)
    {
        if (_hostProcess is null || _hostProcess.HasExited)
        {
            ClearHandles();
            return new ForeignAppShutdownResult(true, "PaneHost had already exited");
        }

        await _protocolLock.WaitAsync(token);
        try
        {
            if (!SendCommand(command))
                return new ForeignAppShutdownResult(false, "could not contact PaneHost");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (await _hostProcess.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Equals(acknowledgement, StringComparison.Ordinal))
                {
                    ClearHandles();
                    return new ForeignAppShutdownResult(true,
                        command == "DETACH" ? "application detached safely" : "application close requested safely");
                }
                if (line.StartsWith("ERROR=", StringComparison.Ordinal))
                {
                    Status = line["ERROR=".Length..];
                    return new ForeignAppShutdownResult(false, Status);
                }
            }

            Status = "PaneHost exited without confirming " + command.ToLowerInvariant();
            return new ForeignAppShutdownResult(false, Status);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Status = $"PaneHost did not confirm {command.ToLowerInvariant()} within 10 seconds";
            return new ForeignAppShutdownResult(false, Status);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Status = "PaneHost shutdown protocol failed: " + ex.Message;
            return new ForeignAppShutdownResult(false, Status);
        }
        finally
        {
            _protocolLock.Release();
        }
    }

    private void ClearHandles()
    {
        Hwnd = WindowHandle.None;
        ChildHwnd = WindowHandle.None;
        EffectiveStrategy = null;
    }

    private bool SendCommand(string command)
    {
        try
        {
            if (_hostProcess is { HasExited: false })
            {
                _hostProcess.StandardInput.WriteLine(command);
                _hostProcess.StandardInput.Flush();
                return true;
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        return false;
    }
}

internal sealed record ForeignAppSwitchResult(bool Succeeded, HostStrategy? EffectiveStrategy, string Message);
internal sealed record ForeignAppShutdownResult(bool Succeeded, string Message);
