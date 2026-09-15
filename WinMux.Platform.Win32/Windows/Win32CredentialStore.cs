using System.Runtime.InteropServices;
using System.Text;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Windows Credential Manager.
///
/// The system's own store: entries are encrypted to the signed-in user, roam with a Microsoft
/// account if the user has one, and are visible and deletable in Control Panel — which matters,
/// because a user who wants a password WinMux saved to be gone should not have to trust WinMux to
/// remove it.
///
/// Saved as <c>CRED_TYPE_GENERIC</c> with <c>CRED_PERSIST_LOCAL_MACHINE</c>. Generic because this
/// is not a Windows logon credential and must not be offered as one; local-machine rather than
/// session so it survives signing out, which is the entire point of saving it.
/// </summary>
public sealed class Win32CredentialStore : ICredentialStore
{
    /// <summary>Credential Manager's own cap on a secret, in bytes.</summary>
    private const int MaximumSecretBytes = 2560;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public bool TrySave(string target, StoredCredential credential, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(credential);

        var blob = Encoding.Unicode.GetBytes(credential.Secret);
        if (blob.Length > MaximumSecretBytes)
        {
            error = $"the secret is longer than Windows Credential Manager allows ({MaximumSecretBytes} bytes)";
            return false;
        }

        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var native = new NativeMethods.Credential
            {
                Type = NativeMethods.CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = NativeMethods.CredPersistLocalMachine,
                UserName = string.IsNullOrEmpty(credential.User) ? null : credential.User,
                Comment = "Saved by WinMux",
            };

            if (NativeMethods.CredWrite(ref native, 0))
            {
                error = null;
                return true;
            }

            error = Describe(Marshal.GetLastWin32Error());
            return false;
        }
        finally
        {
            // Zero the copy we made before releasing it. The string it came from cannot be cleared,
            // but there is no reason to leave a second plaintext copy lying in the heap.
            Array.Clear(blob);
            handle.Free();
        }
    }

    public StoredCredential? Load(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (!NativeMethods.CredRead(target, NativeMethods.CredTypeGeneric, 0, out var pointer))
            return null;

        try
        {
            var native = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);

            if (native.CredentialBlobSize == 0 || native.CredentialBlob == nint.Zero)
                return new StoredCredential(native.UserName ?? string.Empty, string.Empty);

            var blob = ReadBlob(native.CredentialBlob, (int)native.CredentialBlobSize);
            try
            {
                return new StoredCredential(
                    native.UserName ?? string.Empty,
                    Encoding.Unicode.GetString(blob));
            }
            finally
            {
                Array.Clear(blob);
            }
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public bool Delete(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return NativeMethods.CredDelete(target, NativeMethods.CredTypeGeneric, 0);
    }

    private static byte[] ReadBlob(nint source, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(source, bytes, 0, length);
        return bytes;
    }

    /// <summary>The few failures worth wording, rather than a bare error number.</summary>
    private static string Describe(int error) => error switch
    {
        1168 => "no such credential",                                   // ERROR_NOT_FOUND
        1783 => "Windows rejected the credential as malformed",         // RPC_X_BAD_STUB_DATA
        5 => "access was denied by Windows Credential Manager",         // ERROR_ACCESS_DENIED
        _ => $"Windows Credential Manager refused it (error {error})",
    };

    private static class NativeMethods
    {
        private const string Advapi32 = "advapi32.dll";

        internal const uint CredTypeGeneric = 1;
        internal const uint CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public nint CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public nint Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        }

        [DllImport(Advapi32, EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport(Advapi32, EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CredRead(string target, uint type, uint flags, out nint credential);

        [DllImport(Advapi32, EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport(Advapi32, EntryPoint = "CredFree")]
        internal static extern void CredFree(nint buffer);
    }
}
