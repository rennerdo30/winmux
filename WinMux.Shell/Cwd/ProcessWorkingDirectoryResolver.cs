using WinMux.Core.Model;
using WinMux.Platform;

namespace WinMux.Shell.Cwd;

/// <summary>
/// The outcome of querying a process tree for its current working directory.
/// </summary>
/// <param name="Path">The captured path, or <see langword="null"/> when no PEB could be read.</param>
/// <param name="Provenance">Which process supplied <paramref name="Path"/>.</param>
/// <param name="Error">A user-presentable explanation when capture failed.</param>
public sealed record ProcessWorkingDirectoryResult(
    string? Path,
    CwdSource Provenance,
    string? Error)
{
    public bool Succeeded => !string.IsNullOrWhiteSpace(Path);
}

/// <summary>
/// Strategy 2 of CLAUDE.md section 4: walk to the deepest process in a pane's tree and ask the OS
/// what its working directory is, falling back to the root process. See ADR 0004.
///
/// After the Phase 5 extraction this class is pure policy — which process to ask, in what order,
/// and what to make of a refusal. The OS calls live behind <see cref="IProcessInspector"/>, which is
/// what lets the rules that actually cost measurement (skip console infrastructure, prefer the
/// deepest descendant, tie-break by PID) be tested against an invented process tree instead of
/// whatever happens to be running on the machine.
/// </summary>
public sealed class ProcessWorkingDirectoryResolver
{
    private readonly IProcessInspector _processes;

    public ProcessWorkingDirectoryResolver(IProcessInspector processes) =>
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));

    /// <summary>
    /// Attempts to capture a live process working directory without throwing.
    /// </summary>
    /// <param name="rootProcessId">PID of the process that owns the pane.</param>
    /// <param name="disablePebForWsl">
    /// Set for WSL panes. A Windows PEB cannot contain the Linux shell's working directory, so
    /// querying it would return a plausible but incorrect Windows path.
    /// </param>
    public ProcessWorkingDirectoryResult Resolve(int rootProcessId, bool disablePebForWsl)
    {
        if (disablePebForWsl)
        {
            return Failure("PEB working-directory capture is disabled for WSL panes; use an OSC shell report instead.");
        }

        if (rootProcessId <= 0)
        {
            return Failure($"Process ID {rootProcessId} is not valid.");
        }

        try
        {
            var snapshot = _processes.SnapshotProcesses(out var snapshotError);
            var root = snapshot.FirstOrDefault(process => process.ProcessId == rootProcessId);
            if (root.ProcessId == 0)
            {
                return Failure(snapshotError is null
                    ? $"Process {rootProcessId} does not exist or has already exited."
                    : $"Could not inspect process {rootProcessId}: {snapshotError}");
            }

            var deepest = FindDeepestDescendant(rootProcessId, snapshot);
            var errors = new List<string>();

            if (deepest is { } descendant)
            {
                var descendantPath = _processes.TryReadWorkingDirectory(descendant.ProcessId, out var error);
                if (descendantPath is not null)
                {
                    return new ProcessWorkingDirectoryResult(descendantPath, CwdSource.ProcessDeepest, null);
                }

                errors.Add($"deepest descendant {Describe(descendant)}: {error}");
            }

            var rootPath = _processes.TryReadWorkingDirectory(rootProcessId, out var rootError);
            if (rootPath is not null)
            {
                return new ProcessWorkingDirectoryResult(rootPath, CwdSource.ProcessRoot, null);
            }

            errors.Add($"root process {Describe(root)}: {rootError}");
            return Failure(string.Join("; ", errors));
        }
        catch (Exception exception)
        {
            return Failure($"Could not inspect process {rootProcessId}: {exception.Message}");
        }
    }

    internal static ProcessSnapshotEntry? FindDeepestDescendant(
        int rootProcessId,
        IReadOnlyList<ProcessSnapshotEntry> processes)
    {
        var byParent = processes
            .GroupBy(process => process.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var visited = new HashSet<int> { rootProcessId };
        var queue = new Queue<(int ProcessId, int Depth)>();
        var descendants = new List<(ProcessSnapshotEntry Process, int Depth)>();
        queue.Enqueue((rootProcessId, 0));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!byParent.TryGetValue(current.ProcessId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!visited.Add(child.ProcessId))
                {
                    continue;
                }

                var depth = current.Depth + 1;
                descendants.Add((child, depth));
                queue.Enqueue((child.ProcessId, depth));
            }
        }

        // Toolhelp does not expose process creation time. Match the measured spike's deterministic
        // tie-break: choose the highest PID among equally deep descendants.
        return descendants
            // Console infrastructure is not pane intent. It commonly has the same depth as the
            // actual workload and reports C:\Windows, so a PID tie-break can otherwise select a
            // confidently wrong cwd.
            .Where(item => !IsConsoleInfrastructure(item.Process.ExecutableName))
            .OrderByDescending(item => item.Depth)
            .ThenByDescending(item => item.Process.ProcessId)
            .Select(item => (ProcessSnapshotEntry?)item.Process)
            .FirstOrDefault();
    }

    private static bool IsConsoleInfrastructure(string executableName) =>
        executableName.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
        executableName.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase) ||
        executableName.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase);

    private static string Describe(ProcessSnapshotEntry process) =>
        $"{process.ExecutableName} ({process.ProcessId})";

    private static ProcessWorkingDirectoryResult Failure(string error) =>
        new(null, CwdSource.Unknown, error);
}
