using System.Diagnostics;
using Avalonia.Threading;
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
    private readonly bool _restoring;
    private volatile bool _ready;
    private volatile bool _closing;
    private uint? _adoptPid;
    private long? _adoptStartTicks;

    public event Action? StateChanged;
    public event Action<WindowHandle>? Exited;

    public PaneId Id { get; }
    public Pane Pane { get; }
    public WindowHandle Hwnd { get; private set; }
    public WindowHandle ChildHwnd { get; private set; }
    public HostStrategy? EffectiveStrategy { get; private set; }
    public string Status { get; private set; } = "starting…";
    public string? Notice { get; private set; }
    public bool IsReady => _ready;
    public bool Located => _windows.IsAlive(Hwnd);

    public ForeignAppPane(Pane pane, IHostWindowService windows, bool restoring = false)
    {
        Pane = pane;
        Id = pane.Id;
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _restoring = restoring;
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
        var isolatedQuirks = Path.Combine(WinMux.Core.WinMuxDataDirectory.RoamingPath, "foreign-app-quirks.json");
        var quirksPath = WinMux.Core.WinMuxDataDirectory.IsIsolated && File.Exists(isolatedQuirks)
            ? isolatedQuirks
            : Path.Combine(AppContext.BaseDirectory, "foreign-app-quirks.json");
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
        uint expectedPid = 0;
        long expectedStart = 0;
        var hasIdentity = restore.Extras.TryGetValue("adopt_pid", out var pidText) &&
                          uint.TryParse(pidText, out expectedPid) &&
                          restore.Extras.TryGetValue("adopt_start_ticks", out var startText) &&
                          long.TryParse(startText, out expectedStart);
        if (adopting && _restoring && (!hasIdentity || !Settings.ShellSettings.Current.ReattachSurvivingApps))
        {
            Status = hasIdentity
                ? "saved application is on the desktop; reattachment is disabled in settings. Launch a new copy explicitly."
                : "saved application identity is unavailable; WinMux did not launch another copy. Launch explicitly if needed.";
            return;
        }
        if (adopting)
        {
            start.ArgumentList.Add("--adopt");
            start.ArgumentList.Add(adoptText!);
            if (hasIdentity)
            {
                start.ArgumentList.Add("--adopt-pid");
                start.ArgumentList.Add(expectedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add("--adopt-start");
                start.ArgumentList.Add(expectedStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
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
            var host = _hostProcess;
            host.EnableRaisingEvents = true;
            host.Exited += (_, _) => _ = ObserveUnexpectedExitAsync(host);
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
                    if (TryReadIdentity(line, out var pid, out var ticks))
                    {
                        _adoptPid = pid;
                        _adoptStartTicks = ticks;
                        continue;
                    }
                    if (TryReadReady(line, out var child, out var effectiveStrategy))
                    {
                        if (!line.Contains("strategy=", StringComparison.OrdinalIgnoreCase))
                            effectiveStrategy = reportedStrategy;
                        ChildHwnd = child;
                        EffectiveStrategy = effectiveStrategy;
                        _ready = true;
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

    private static bool TryReadIdentity(string line, out uint pid, out long ticks)
    {
        pid = 0;
        ticks = 0;
        if (!line.StartsWith("IDENTITY=", StringComparison.Ordinal)) return false;
        var parts = line["IDENTITY=".Length..].Split(',', 2);
        return parts.Length == 2 && uint.TryParse(parts[0], out pid) && long.TryParse(parts[1], out ticks);
    }

    public RestoreDescriptor CaptureRestoreDescriptor()
    {
        var extras = new Dictionary<string, string>(Pane.Restore.Extras, StringComparer.Ordinal);
        if (_ready && !ChildHwnd.IsNone && _adoptPid is { } pid && _adoptStartTicks is { } ticks)
        {
            extras["adopt_window"] = ChildHwnd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            extras["adopt_pid"] = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            extras["adopt_start_ticks"] = ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return Pane.Restore with { Extras = extras };
    }

    private async Task ObserveUnexpectedExitAsync(Process? host)
    {
        if (!_ready || _closing || host is null) return;
        try
        {
            await _protocolLock.WaitAsync();
            string remainder;
            try { remainder = await host.StandardOutput.ReadToEndAsync(); }
            finally { _protocolLock.Release(); }
            if (_closing) return;
            var exit = remainder.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.StartsWith("EXITED=", StringComparison.Ordinal));
            Dispatcher.UIThread.Post(() =>
            {
                if (_closing || _hostProcess != host) return;
                Status = exit switch
                {
                    "EXITED=window closed" => "application window closed",
                    { } line => "application exited with code " + line["EXITED=".Length..],
                    _ => "PaneHost exited unexpectedly; application is no longer hosted",
                };
                var child = ChildHwnd;
                ClearHandles();
                Exited?.Invoke(child);
                StateChanged?.Invoke();
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_closing || _hostProcess != host) return;
                Status = "PaneHost exited; could not read application exit: " + ex.Message;
                var child = ChildHwnd;
                ClearHandles();
                Exited?.Invoke(child);
                StateChanged?.Invoke();
            });
        }
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

    public async Task RestartAsync(ISet<WindowHandle> claimed, WindowHandle ownerWindow, CancellationToken token)
    {
        if (_ready) return;
        _hostProcess?.Dispose();
        _hostProcess = null;
        _closing = false;
        _adoptPid = null;
        _adoptStartTicks = null;
        var extras = new Dictionary<string, string>(Pane.Restore.Extras, StringComparer.Ordinal);
        extras.Remove("adopt_window");
        extras.Remove("adopt_pid");
        extras.Remove("adopt_start_ticks");
        Pane.Restore = Pane.Restore with { Extras = extras };
        Notice = null;
        Status = "restarting…";
        StateChanged?.Invoke();
        await LaunchAsync(claimed, ownerWindow, token);
        StateChanged?.Invoke();
    }

    private async Task<ForeignAppShutdownResult> ShutdownAsync(
        string command,
        string acknowledgement,
        CancellationToken token)
    {
        _closing = true;
        var succeeded = false;
        if (_hostProcess is null || _hostProcess.HasExited)
        {
            ClearHandles();
            return new ForeignAppShutdownResult(true, "PaneHost had already exited");
        }

        try { await _protocolLock.WaitAsync(token); }
        catch { _closing = false; throw; }
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
                    succeeded = true;
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
            if (!succeeded) _closing = false;
        }
    }

    private void ClearHandles()
    {
        _ready = false;
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
