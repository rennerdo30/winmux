namespace WinMux.Platform;

/// <summary>One running process, as much of it as the cwd strategy needs.</summary>
/// <param name="ProcessId">The process.</param>
/// <param name="ParentProcessId">Its parent, for walking to the deepest descendant of a pane.</param>
/// <param name="ExecutableName">File name only, never a path — used to skip console infrastructure.</param>
public readonly record struct ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string ExecutableName);

/// <summary>
/// Reads process facts the working-directory strategy needs (CLAUDE.md section 4, strategy 2).
///
/// Extracting this is what makes the *policy* testable: "walk to the deepest descendant, skip
/// console infrastructure, prefer a newer reading" is ordinary logic that previously could only be
/// exercised against whatever happened to be running on the machine.
///
/// Both methods report failure through an out parameter rather than throwing, because a pane whose
/// directory cannot be read must degrade to the next strategy rather than fail a whole save.
/// </summary>
public interface IProcessInspector
{
    /// <summary>
    /// Every process visible to this user, or an empty list with <paramref name="error"/> set.
    /// </summary>
    IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses(out string? error);

    /// <summary>
    /// The process's current working directory, or null with <paramref name="error"/> describing
    /// why in words a user could act on.
    ///
    /// Known to be unreliable by design on Windows: PowerShell's is permanently stale because
    /// Set-Location moves the provider location and not the process directory, and a WSL pane's is
    /// meaningless because the answer lives in the Linux VM (ADR 0004). Neither is this method's
    /// problem to solve — it reports what the OS says, and the caller weighs it.
    /// </summary>
    string? TryReadWorkingDirectory(int processId, out string error);
}
