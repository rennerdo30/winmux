using System.Runtime.InteropServices;
using System.Text;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Windows implementation of <see cref="IProcessInspector"/>: Toolhelp for the process tree, and a
/// PEB read for a working directory.
///
/// Moved here unchanged from the shell during the Phase 5 extraction. Two measured limits are the
/// caller's to weigh, not this class's to hide (ADR 0004): the PEB is **permanently stale for
/// PowerShell**, because `Set-Location` moves the provider location rather than the process
/// directory; and it is **meaningless for a WSL pane**, where it reports a Windows path for a shell
/// whose directory lives in the Linux VM. This reports what Windows says and says why when it
/// cannot.
/// </summary>
public sealed class Win32ProcessInspector : IProcessInspector
{
    public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses(out string? error)
    {
        error = null;
        var processes = new List<ProcessSnapshotEntry>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == nint.Zero || snapshot == NativeMethods.InvalidHandleValue)
        {
            error = Win32Error("CreateToolhelp32Snapshot");
            return processes;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = Marshal.SizeOf<NativeMethods.ProcessEntry32>(),
                ExecutableFile = string.Empty,
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                error = Win32Error("Process32First");
                return processes;
            }

            do
            {
                processes.Add(new ProcessSnapshotEntry(entry.ProcessId, entry.ParentProcessId, entry.ExecutableFile));
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

            return processes;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }
    }

    public string? TryReadWorkingDirectory(int processId, out string error)
    {
        error = string.Empty;
        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, processId);
        if (process == nint.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 5
                ? "access denied (the process may be elevated or protected)"
                : $"OpenProcess failed ({code})";
            return null;
        }

        try
        {
            if (!NativeMethods.IsWow64Process(process, out var isWow64))
            {
                error = Win32Error("IsWow64Process");
                return null;
            }

            if (isWow64)
            {
                error = "target is 32-bit (WOW64); this reader supports only the x64 PEB layout";
                return null;
            }

            var information = new NativeMethods.ProcessBasicInformation();
            var status = NativeMethods.NtQueryInformationProcess(
                process, 0, ref information, Marshal.SizeOf<NativeMethods.ProcessBasicInformation>(), out _);
            if (status != 0)
            {
                error = $"NtQueryInformationProcess returned status 0x{status:X8}";
                return null;
            }

            if (information.PebBaseAddress == nint.Zero)
            {
                error = "the process has no PEB address";
                return null;
            }

            if (!ReadMemory(process, nint.Add(information.PebBaseAddress, NativeMethods.PebProcessParametersOffset),
                    sizeof(long), out var processParametersBytes, out error))
            {
                return null;
            }

            var processParameters = new nint(BitConverter.ToInt64(processParametersBytes));
            if (processParameters == nint.Zero)
            {
                error = "the PEB has no ProcessParameters address";
                return null;
            }

            // x64 UNICODE_STRING: USHORT Length, USHORT MaximumLength, 4 bytes padding,
            // followed by an eight-byte buffer pointer.
            if (!ReadMemory(process, nint.Add(processParameters, NativeMethods.ParametersCurrentDirectoryOffset),
                    16, out var unicodeString, out error))
            {
                return null;
            }

            var byteLength = BitConverter.ToUInt16(unicodeString);
            var pathAddress = new nint(BitConverter.ToInt64(unicodeString, 8));
            if (byteLength == 0 || pathAddress == nint.Zero)
            {
                error = "the PEB CurrentDirectory is empty";
                return null;
            }

            if (!ReadMemory(process, pathAddress, byteLength, out var pathBytes, out error)) return null;

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
            _ = NativeMethods.CloseHandle(process);
        }
    }

    private static bool ReadMemory(nint process, nint address, int byteCount, out byte[] bytes, out string error)
    {
        bytes = new byte[byteCount];
        if (NativeMethods.ReadProcessMemory(process, address, bytes, new nint(byteCount), out var bytesRead)
            && bytesRead.ToInt64() == byteCount)
        {
            error = string.Empty;
            return true;
        }

        error = $"ReadProcessMemory at 0x{address.ToInt64():X} failed ({Marshal.GetLastWin32Error()})";
        return false;
    }

    private static string Win32Error(string operation) => $"{operation} failed ({Marshal.GetLastWin32Error()})";

    private static class NativeMethods
    {
        private const string Kernel32 = "kernel32.dll";

        internal const uint TH32CS_SNAPPROCESS = 0x00000002;
        internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
        internal const uint PROCESS_VM_READ = 0x0010;
        internal const int PebProcessParametersOffset = 0x20;
        internal const int ParametersCurrentDirectoryOffset = 0x38;
        internal static readonly nint InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry32
        {
            internal int Size;
            internal int UsageCount;
            internal int ProcessId;
            internal nint DefaultHeapId;
            internal int ModuleId;
            internal int ThreadCount;
            internal int ParentProcessId;
            internal int BasePriority;
            internal int Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string ExecutableFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessBasicInformation
        {
            internal nint ExitStatus;
            internal nint PebBaseAddress;
            internal nint AffinityMask;
            internal nint BasePriority;
            internal nint UniqueProcessId;
            internal nint InheritedFromUniqueProcessId;
        }

        [DllImport(Kernel32, SetLastError = true)]
        internal static extern nint CreateToolhelp32Snapshot(uint flags, int processId);

        [DllImport(Kernel32, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

        [DllImport(Kernel32, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);

        [DllImport(Kernel32, SetLastError = true)]
        internal static extern nint OpenProcess(
            uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadProcessMemory(
            nint process, nint baseAddress, byte[] buffer, nint size, out nint bytesRead);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process(nint process, [MarshalAs(UnmanagedType.Bool)] out bool isWow64);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            nint process, int informationClass, ref ProcessBasicInformation information,
            int informationLength, out int returnLength);
    }
}
