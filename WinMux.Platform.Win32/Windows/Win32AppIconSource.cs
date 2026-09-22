using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Application icons, taken from the shell's own image lists.
///
/// <para>
/// The obvious call is <c>Icon.ExtractAssociatedIcon</c>, and it is the wrong one: it returns 32×32.
/// On a 150% display that is drawn at 48 logical pixels from 32 real ones, and the result looks like
/// a thumbnail of an icon rather than an icon. The shell keeps larger renditions — 48 and 256 — in
/// its system image lists, which is where Explorer and the Start menu get theirs, so this asks for
/// those and falls back down the sizes rather than up.
/// </para>
///
/// <para>
/// Icons are cached by path and size. The picker draws a few hundred rows and a user scrolls the
/// list more than once; extracting on every pass would make scrolling the slowest thing in the
/// application.
/// </para>
/// </summary>
public sealed class Win32AppIconSource : IAppIconSource
{
    private readonly Dictionary<string, byte[]?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool IsAvailable => OperatingSystem.IsWindows();

    public byte[]? GetIconPng(string programPath, int size = 32)
    {
        if (string.IsNullOrWhiteSpace(programPath)) return null;

        var key = $"{programPath}|{size}";
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var png = Extract(programPath, size);

        lock (_gate)
        {
            // A null is worth caching too: an executable with no icon would otherwise be re-probed
            // on every redraw, which is the expensive case rather than the cheap one.
            _cache[key] = png;
        }

        return png;
    }

    private static byte[]? Extract(string programPath, int size)
    {
        try
        {
            // Largest first: a 256px icon scaled down is crisp at any display scale, whereas a 32px
            // one scaled up never is.
            foreach (var list in PreferredLists(size))
            {
                if (TryFromImageList(programPath, list, 0, 0, out var png)) return png;
            }

            return FromAssociatedIcon(programPath);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                      or ArgumentException or ExternalException)
        {
            // A missing icon is an ordinary answer. The picker shows a placeholder.
            return null;
        }
    }

    internal static IEnumerable<int> PreferredLists(int size) =>
        size > 48
            ? [NativeMethods.ShilJumbo, NativeMethods.ShilExtraLarge, NativeMethods.ShilLarge]
            : [NativeMethods.ShilExtraLarge, NativeMethods.ShilJumbo, NativeMethods.ShilLarge];

    /// <param name="path">The file to ask about, or a made-up name when using file attributes.</param>
    /// <param name="list">Which system image list, and so which size, to take the icon from.</param>
    /// <param name="fileAttributes">Passed through to <c>SHGetFileInfo</c>; see <paramref name="extraFlags"/>.</param>
    /// <param name="extraFlags">
    /// <c>SHGFI_USEFILEATTRIBUTES</c> makes the shell answer from the name and the attributes alone,
    /// without touching the file — which is what lets a remote file have a type icon at all.
    /// </param>
    /// <param name="png">The icon, when there was one.</param>
    internal static bool TryFromImageList(string path, int list, uint fileAttributes, uint extraFlags, out byte[]? png)
    {
        png = null;

        var info = default(NativeMethods.ShFileInfo);
        var result = NativeMethods.SHGetFileInfo(
            path, fileAttributes, ref info, (uint)Marshal.SizeOf<NativeMethods.ShFileInfo>(),
            NativeMethods.ShgfiSysIconIndex | extraFlags);

        if (result == nint.Zero) return false;

        if (NativeMethods.SHGetImageList(list, NativeMethods.ImageListIid, out var images) != 0 ||
            images is null)
        {
            return false;
        }

        try
        {
            if (images.GetIcon(info.IconIndex, NativeMethods.IldTransparent, out var icon) != 0 ||
                icon == nint.Zero)
            {
                return false;
            }

            try
            {
                png = ToPng(icon);
                return png is not null;
            }
            finally
            {
                NativeMethods.DestroyIcon(icon);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(images);
        }
    }

    /// <summary>
    /// The shell's name for a type, asked by name and attributes alone (<c>SHGFI_TYPENAME</c> with
    /// <c>SHGFI_USEFILEATTRIBUTES</c>), so it needs no file on disk.
    /// </summary>
    internal static string? TypeName(string path, uint fileAttributes)
    {
        var info = default(NativeMethods.ShFileInfo);
        var result = NativeMethods.SHGetFileInfo(
            path, fileAttributes, ref info, (uint)Marshal.SizeOf<NativeMethods.ShFileInfo>(),
            NativeMethods.ShgfiTypeName | NativeMethods.ShgfiUseFileAttributes);

        return result == nint.Zero || string.IsNullOrWhiteSpace(info.TypeName) ? null : info.TypeName;
    }

    private static byte[]? FromAssociatedIcon(string path)
    {
        if (!File.Exists(path)) return null;

        using var icon = Icon.ExtractAssociatedIcon(path);
        if (icon is null) return null;

        using var bitmap = icon.ToBitmap();
        return Encode(bitmap);
    }

    private static byte[]? ToPng(nint hIcon)
    {
        using var icon = Icon.FromHandle(hIcon);
        using var bitmap = icon.ToBitmap();
        return Encode(bitmap);
    }

    private static byte[]? Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.Length == 0 ? null : stream.ToArray();
    }

    private static class NativeMethods
    {
        internal const uint ShgfiSysIconIndex = 0x4000;
        internal const uint ShgfiTypeName = 0x400;
        internal const uint ShgfiUseFileAttributes = 0x10;
        internal const int IldTransparent = 0x1;

        // Shell image list sizes: 0 large (32), 2 extra large (48), 4 jumbo (256).
        internal const int ShilLarge = 0;
        internal const int ShilExtraLarge = 2;
        internal const int ShilJumbo = 4;

        internal static readonly Guid ImageListIid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ShFileInfo
        {
            public nint Icon;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint SHGetFileInfo(
            string path, uint fileAttributes, ref ShFileInfo info, uint size, uint flags);

        [DllImport("shell32.dll")]
        internal static extern int SHGetImageList(
            int imageList, in Guid riid, out IImageList? images);

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(nint icon);

        /// <summary>
        /// Only the one method is declared, and the order of the others matters: this is a vtable,
        /// so every method before <c>GetIcon</c> has to be present as a placeholder or the slot
        /// numbers shift and the call lands somewhere else entirely.
        /// </summary>
        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IImageList
        {
            void Add();
            void ReplaceIcon();
            void SetOverlayImage();
            void Replace();
            void AddMasked();
            void Draw();
            void Remove();

            /// <summary>
            /// <c>HRESULT GetIcon(int i, UINT flags, HICON *picon)</c>. The handle comes back
            /// through the out parameter, not the return value — the return value is the HRESULT,
            /// and reading it as a handle would hand a status code to <c>Icon.FromHandle</c>.
            /// </summary>
            [PreserveSig]
            int GetIcon(int index, int flags, out nint icon);
        }
    }
}
