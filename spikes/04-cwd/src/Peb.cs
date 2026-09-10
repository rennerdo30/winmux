using System.Runtime.InteropServices;

namespace CwdSpike;

/// <summary>
/// Strategy 2 from CLAUDE.md section 4: read a process's current directory out of its PEB.
/// Works without any shell cooperation, but needs matching bitness and access rights.
/// </summary>
internal static class Peb
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    // x64 offsets. A 32-bit target has a different PEB layout entirely and is not handled here —
    // that is one of the documented limits of this strategy, not an oversight.
    private const int PEB_PROCESS_PARAMETERS = 0x20;
    private const int PARAMS_CURRENT_DIRECTORY = 0x38;   // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION info, int len, out int ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr h, out bool wow64);

    /// <summary>The reason a read failed, so the spike can report *why* rather than just "no".</summary>
    public static string LastError = "";

    public static string? TryReadCurrentDirectory(int pid)
    {
        LastError = "";
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
        {
            var e = Marshal.GetLastWin32Error();
            LastError = e == 5 ? "access denied (elevation or protected process)" : "OpenProcess failed (" + e + ")";
            return null;
        }
        try
        {
            if (IsWow64Process(h, out var wow64) && wow64)
            {
                LastError = "target is 32-bit (WOW64); this reader only handles the x64 PEB layout";
                return null;
            }

            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0) { LastError = "NtQueryInformationProcess status 0x" + status.ToString("X8"); return null; }
            if (pbi.PebBaseAddress == IntPtr.Zero) { LastError = "no PEB address"; return null; }

            if (!Read(h, pbi.PebBaseAddress + PEB_PROCESS_PARAMETERS, 8, out var p)) return null;
            var paramsPtr = (IntPtr)BitConverter.ToInt64(p, 0);
            if (paramsPtr == IntPtr.Zero) { LastError = "no ProcessParameters"; return null; }

            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; ULONG pad; PWSTR Buffer; }
            if (!Read(h, paramsPtr + PARAMS_CURRENT_DIRECTORY, 16, out var us)) return null;
            int len = BitConverter.ToUInt16(us, 0);
            var bufPtr = (IntPtr)BitConverter.ToInt64(us, 8);
            if (len <= 0 || bufPtr == IntPtr.Zero) { LastError = "empty CurrentDirectory"; return null; }
            if (len > 64 * 1024) { LastError = "implausible CurrentDirectory length " + len; return null; }

            if (!Read(h, bufPtr, len, out var raw)) return null;
            return System.Text.Encoding.Unicode.GetString(raw).TrimEnd('\0', '\\') is { Length: > 0 } s ? s : null;
        }
        finally { CloseHandle(h); }
    }

    private static bool Read(IntPtr h, IntPtr addr, int count, out byte[] buf)
    {
        buf = new byte[count];
        if (ReadProcessMemory(h, addr, buf, new IntPtr(count), out var got) && (int)got == count) return true;
        LastError = "ReadProcessMemory at 0x" + addr.ToString("X") + " failed (" + Marshal.GetLastWin32Error() + ")";
        return false;
    }
}
