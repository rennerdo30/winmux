using System.Runtime.InteropServices;
using System.Text;
using WinMux.Core.Model;

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
/// Resolves the working directory stored in the PEB of the deepest process in a tree, falling
/// back to the root process when the descendant cannot be read. See ADR 0004.
/// </summary>
public static class ProcessWorkingDirectoryResolver
{
    /// <summary>
    /// Attempts to capture a live process working directory without throwing.
    /// </summary>
    /// <param name="rootProcessId">PID of the process that owns the pane.</param>
    /// <param name="disablePebForWsl">
    /// Set for WSL panes. A Windows PEB cannot contain the Linux shell's working directory, so
    /// querying it would return a plausible but incorrect Windows path.
    /// </param>
    public static ProcessWorkingDirectoryResult Resolve(int rootProcessId, bool disablePebForWsl)
    {
        if (disablePebForWsl)
        {
            return Failure("PEB working-directory capture is disabled for WSL panes; use an OSC shell report instead.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return Failure("PEB working-directory capture is supported only on Windows.");
        }

        if (rootProcessId <= 0)
        {
            return Failure($"Process ID {rootProcessId} is not valid.");
        }

        try
        {
            var snapshot = NativeMethods.SnapshotProcesses(out var snapshotError);
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
                var descendantPath = NativeMethods.TryReadCurrentDirectory(descendant.ProcessId, out var error);
                if (descendantPath is not null)
                {
                    return new ProcessWorkingDirectoryResult(descendantPath, CwdSource.ProcessDeepest, null);
                }

                errors.Add($"deepest descendant {Describe(descendant)}: {error}");
            }

            var rootPath = NativeMethods.TryReadCurrentDirectory(rootProcessId, out var rootError);
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

    private static ProcessEntry? FindDeepestDescendant(int rootProcessId, IReadOnlyList<ProcessEntry> processes)
    {
        var byParent = processes
            .GroupBy(process => process.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var visited = new HashSet<int> { rootProcessId };
        var queue = new Queue<(int ProcessId, int Depth)>();
        var descendants = new List<(ProcessEntry Process, int Depth)>();
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
            .Select(item => (ProcessEntry?)item.Process)
            .FirstOrDefault();
    }

    private static bool IsConsoleInfrastructure(string executableName) =>
        executableName.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
        executableName.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase) ||
        executableName.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase);

    private static string Describe(ProcessEntry process) =>
        $"{process.ExecutableName} ({process.ProcessId})";

    private static ProcessWorkingDirectoryResult Failure(string error) =>
        new(null, CwdSource.Unknown, error);

    private readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string ExecutableName);

    private static class NativeMethods
    {
        private const uint Th32csSnapProcess = 0x00000002;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const int PebProcessParametersOffset = 0x20;
        private const int ParametersCurrentDirectoryOffset = 0x38;
        private static readonly IntPtr InvalidHandleValue = new(-1);

        internal static List<ProcessEntry> SnapshotProcesses(out string? error)
        {
            error = null;
            var processes = new List<ProcessEntry>();
            var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            {
                error = Win32Error("CreateToolhelp32Snapshot");
                return processes;
            }

            try
            {
                var entry = new ProcessEntry32
                {
                    Size = Marshal.SizeOf<ProcessEntry32>(),
                    ExecutableFile = string.Empty,
                };

                if (!Process32First(snapshot, ref entry))
                {
                    error = Win32Error("Process32First");
                    return processes;
                }

                do
                {
                    processes.Add(new ProcessEntry(
                        entry.ProcessId,
                        entry.ParentProcessId,
                        entry.ExecutableFile));
                }
                while (Process32Next(snapshot, ref entry));

                return processes;
            }
            finally
            {
                _ = CloseHandle(snapshot);
            }
        }

        internal static string? TryReadCurrentDirectory(int processId, out string error)
        {
            error = string.Empty;
            var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            if (process == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                error = code == 5
                    ? "access denied (the process may be elevated or protected)"
                    : $"OpenProcess failed ({code})";
                return null;
            }

            try
            {
                if (!IsWow64Process(process, out var isWow64))
                {
                    error = Win32Error("IsWow64Process");
                    return null;
                }

                if (isWow64)
                {
                    error = "target is 32-bit (WOW64); this reader supports only the x64 PEB layout";
                    return null;
                }

                var information = new ProcessBasicInformation();
                var status = NtQueryInformationProcess(
                    process,
                    0,
                    ref information,
                    Marshal.SizeOf<ProcessBasicInformation>(),
                    out _);
                if (status != 0)
                {
                    error = $"NtQueryInformationProcess returned status 0x{status:X8}";
                    return null;
                }

                if (information.PebBaseAddress == IntPtr.Zero)
                {
                    error = "the process has no PEB address";
                    return null;
                }

                if (!ReadMemory(
                        process,
                        IntPtr.Add(information.PebBaseAddress, PebProcessParametersOffset),
                        sizeof(long),
                        out var processParametersBytes,
                        out error))
                {
                    return null;
                }

                var processParameters = new IntPtr(BitConverter.ToInt64(processParametersBytes));
                if (processParameters == IntPtr.Zero)
                {
                    error = "the PEB has no ProcessParameters address";
                    return null;
                }

                // x64 UNICODE_STRING: USHORT Length, USHORT MaximumLength, 4 bytes padding,
                // followed by an eight-byte buffer pointer.
                if (!ReadMemory(
                        process,
                        IntPtr.Add(processParameters, ParametersCurrentDirectoryOffset),
                        16,
                        out var unicodeString,
                        out error))
                {
                    return null;
                }

                var byteLength = BitConverter.ToUInt16(unicodeString);
                var pathAddress = new IntPtr(BitConverter.ToInt64(unicodeString, 8));
                if (byteLength == 0 || pathAddress == IntPtr.Zero)
                {
                    error = "the PEB CurrentDirectory is empty";
                    return null;
                }

                if (!ReadMemory(process, pathAddress, byteLength, out var pathBytes, out error))
                {
                    return null;
                }

                var path = Encoding.Unicode.GetString(pathBytes).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(path))
                {
                    error = "the PEB CurrentDirectory is empty";
                    return null;
                }

                return path;
            }
            finally
            {
                _ = CloseHandle(process);
            }
        }

        private static bool ReadMemory(
            IntPtr process,
            IntPtr address,
            int byteCount,
            out byte[] bytes,
            out string error)
        {
            bytes = new byte[byteCount];
            if (ReadProcessMemory(process, address, bytes, new IntPtr(byteCount), out var bytesRead)
                && bytesRead.ToInt64() == byteCount)
            {
                error = string.Empty;
                return true;
            }

            error = $"ReadProcessMemory at 0x{address.ToInt64():X} failed ({Marshal.GetLastWin32Error()})";
            return false;
        }

        private static string Win32Error(string operation) =>
            $"{operation} failed ({Marshal.GetLastWin32Error()})";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            internal int Size;
            internal int UsageCount;
            internal int ProcessId;
            internal IntPtr DefaultHeapId;
            internal int ModuleId;
            internal int ThreadCount;
            internal int ParentProcessId;
            internal int BasePriority;
            internal int Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string ExecutableFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            internal IntPtr ExitStatus;
            internal IntPtr PebBaseAddress;
            internal IntPtr AffinityMask;
            internal IntPtr BasePriority;
            internal IntPtr UniqueProcessId;
            internal IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(
            IntPtr process,
            IntPtr baseAddress,
            byte[] buffer,
            IntPtr size,
            out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool isWow64);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr process,
            int informationClass,
            ref ProcessBasicInformation information,
            int informationLength,
            out int returnLength);
    }
}
