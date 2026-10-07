using WinMux.Platform;

namespace WinMux.Shell.Panes;

/// <summary>
/// A close-time check, not a polling loop. Call off the UI thread with a fresh process snapshot:
/// a stale cwd cache can miss a program started just before the user clicks close.
/// </summary>
internal static class TerminalCloseProtection
{
    internal sealed record Assessment(bool RequiresConfirmation, string Description);

    internal static IReadOnlyDictionary<int, Assessment> Inspect(
        IEnumerable<int> roots, IProcessInspector inspector)
    {
        var ids = roots.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<int, Assessment>();
        try
        {
            var snapshot = inspector.SnapshotProcesses(out var error);
            return ids.ToDictionary(id => id, id => string.IsNullOrWhiteSpace(error)
                ? Assess(id, snapshot)
                : new Assessment(true, "could not check running programs: " + error));
        }
        catch (Exception ex)
        {
            return ids.ToDictionary(id => id,
                _ => new Assessment(true, "could not check running programs: " + ex.Message));
        }
    }

    internal static Assessment Assess(int root, IReadOnlyList<ProcessSnapshotEntry> snapshot)
    {
        var process = snapshot.FirstOrDefault(entry => entry.ProcessId == root);
        // A successful fresh snapshot that no longer contains the process means it exited.
        if (process.ProcessId == 0) return new(false, "terminal has exited");

        var children = snapshot.GroupBy(entry => entry.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var visited = new HashSet<int> { root };
        var pending = new Queue<int>();
        var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent, out var descendants)) continue;
            foreach (var child in descendants)
            {
                if (!visited.Add(child.ProcessId)) continue;
                // Include GUI children too: a process tree does not establish whether their
                // work is independent of the shell. Only known console infrastructure is ignored.
                pending.Enqueue(child.ProcessId);
                if (!Infrastructure(child.ExecutableName)) programs.Add(child.ExecutableName);
            }
        }

        // A program launched directly in a terminal (ssh, node, claude, etc.) has no shell child.
        // WSL's Linux children cannot be seen in this snapshot; protect WSL conservatively too.
        if (!IdleShell(process.ExecutableName)) programs.Add(process.ExecutableName);
        if (process.ExecutableName.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase))
            return new(true, "wsl.exe session (Linux programs cannot be checked from Windows)");
        return programs.Count == 0
            ? new(false, "shell has no running child programs")
            : new(true, "running programs: " + string.Join(", ", programs.Order(StringComparer.OrdinalIgnoreCase)));
    }

    private static bool IdleShell(string name) =>
        name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    private static bool Infrastructure(string name) =>
        name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase);
}
