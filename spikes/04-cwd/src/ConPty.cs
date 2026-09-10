using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CwdSpike;

/// <summary>
/// Raw ConPTY. Deliberately hand-rolled rather than taken from a package: stage A of the spike
/// is about establishing what the platform itself costs, before any library is in the picture.
/// </summary>
internal static class Native
{
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new(0x00020016);

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    private const string K = "kernel32.dll";

    [DllImport(K, SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phPC);

    [DllImport(K, SetLastError = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport(K, SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport(K, SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport(K, SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport(K, SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport(K, SetLastError = true)]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport(K, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(
        string? lpApplicationName, char[] lpCommandLine, IntPtr procAttrs, IntPtr threadAttrs,
        bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport(K, SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport(K, SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport(K, SetLastError = true)] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport(K, SetLastError = true)] public static extern bool TerminateProcess(IntPtr h, uint code);
}

/// <summary>One pseudoconsole plus the process attached to it.</summary>
internal sealed class PtySession : IDisposable
{
    private IntPtr _hPC;
    private IntPtr _attrList;
    private Native.PROCESS_INFORMATION _pi;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly Thread _reader;
    private volatile bool _disposed;

    public FileStream Input { get; }
    public FileStream Output { get; }
    public int ProcessId => _pi.dwProcessId;

    /// <summary>Raised on the reader thread for every chunk that arrives.</summary>
    public event Action<byte[], int>? DataReceived;

    /// <summary>Raised once when the output pipe reaches end of file.</summary>
    public event Action? Eof;

    public PtySession(string commandLine, short cols, short rows, string? cwd = null, bool suppressStdHandleInheritance = true)
    {
        if (!Native.CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("CreatePipe(input) failed: " + Marshal.GetLastWin32Error());
        if (!Native.CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("CreatePipe(output) failed: " + Marshal.GetLastWin32Error());

        var hr = Native.CreatePseudoConsole(new Native.COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out _hPC);
        if (hr != 0) throw new InvalidOperationException("CreatePseudoConsole failed: HRESULT 0x" + hr.ToString("X8"));

        // Build the attribute list carrying the HPCON.
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attrList = Marshal.AllocHGlobal(size);
        if (!Native.InitializeProcThreadAttributeList(_attrList, 1, 0, ref size))
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());
        if (!Native.UpdateProcThreadAttribute(_attrList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());

        var si = new Native.STARTUPINFOEXW();
        si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEXW>();
        si.lpAttributeList = _attrList;

        // Without this, CreateProcess propagates OUR std handles to the child. When the host is a
        // console app those are our console, and they win over the pseudoconsole: the child attaches
        // to conhost (the title updates) but writes its output to our console instead of the pty.
        // Microsoft's ConPTY sample never hits this because its host is a GUI app with null handles.
        if (suppressStdHandleInheritance)
        {
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = IntPtr.Zero;
            si.StartupInfo.hStdOutput = IntPtr.Zero;
            si.StartupInfo.hStdError = IntPtr.Zero;
        }

        var cmd = (commandLine + "\0").ToCharArray();
        if (!Native.CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                Native.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, cwd, ref si, out _pi))
            throw new InvalidOperationException("CreateProcessW failed: " + Marshal.GetLastWin32Error());

        // The pseudoconsole duplicated these; our copies must go, or the output pipe never sees EOF.
        inputRead.Dispose();
        outputWrite.Dispose();

        Input = new FileStream(_inputWrite, FileAccess.Write);
        Output = new FileStream(_outputRead, FileAccess.Read);

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-reader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (!_disposed)
            {
                int n = Output.Read(buf, 0, buf.Length);
                if (n <= 0) { Eof?.Invoke(); break; }
                var copy = new byte[n];
                Buffer.BlockCopy(buf, 0, copy, 0, n);
                DataReceived?.Invoke(copy, n);
            }
        }
        catch (IOException) { /* pipe closed on shutdown */ }
        catch (ObjectDisposedException) { }
    }

    public void Write(string s) => WriteBytes(System.Text.Encoding.UTF8.GetBytes(s));

    public void WriteBytes(ReadOnlySpan<byte> b)
    {
        lock (Input) { Input.Write(b); Input.Flush(); }
    }

    public void Resize(short cols, short rows)
    {
        var hr = Native.ResizePseudoConsole(_hPC, new Native.COORD { X = cols, Y = rows });
        if (hr != 0) throw new InvalidOperationException("ResizePseudoConsole failed: HRESULT 0x" + hr.ToString("X8"));
    }

    public bool WaitForExit(int ms) => Native.WaitForSingleObject(_pi.hProcess, (uint)ms) == 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pi.hProcess != IntPtr.Zero)
        {
            Native.GetExitCodeProcess(_pi.hProcess, out var code);
            if (code == 259) Native.TerminateProcess(_pi.hProcess, 0); // STILL_ACTIVE
        }

        // Order matters: closing the pseudoconsole before the output pipe is drained can block.
        if (_hPC != IntPtr.Zero) { Native.ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

        try { Output.Dispose(); } catch { }
        try { Input.Dispose(); } catch { }

        if (_attrList != IntPtr.Zero)
        {
            Native.DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
        if (_pi.hThread != IntPtr.Zero) Native.CloseHandle(_pi.hThread);
        if (_pi.hProcess != IntPtr.Zero) Native.CloseHandle(_pi.hProcess);
        _reader.Join(500);
    }
}
