using System.Runtime.InteropServices;

namespace CwdSpike;

/// <summary>
/// "Walk to the deepest child of the pane's process" (CLAUDE.md section 4, strategy 2).
/// The deepest descendant is the one actually sitting at a prompt when shells are nested.
/// </summary>
internal static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    public readonly record struct Entry(int Pid, int ParentPid, string Exe);

    public static List<Entry> Snapshot()
    {
        var list = new List<Entry>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return list;
            do { list.Add(new Entry(e.th32ProcessID, e.th32ParentProcessID, e.szExeFile)); }
            while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    /// <summary>Every descendant of <paramref name="rootPid"/>, nearest first.</summary>
    public static List<Entry> Descendants(int rootPid, List<Entry>? snapshot = null)
    {
        var all = snapshot ?? Snapshot();
        var byParent = all.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<Entry>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!byParent.TryGetValue(pid, out var kids)) continue;
            foreach (var k in kids)
            {
                if (result.Any(r => r.Pid == k.Pid)) continue;   // cycle guard
                result.Add(k);
                queue.Enqueue(k.Pid);
            }
        }
        return result;
    }

    /// <summary>The deepest descendant, or the root itself when it has no children.</summary>
    public static Entry Deepest(int rootPid)
    {
        var all = Snapshot();
        var self = all.FirstOrDefault(p => p.Pid == rootPid, new Entry(rootPid, 0, "?"));
        var kids = Descendants(rootPid, all);
        if (kids.Count == 0) return self;

        // Depth = distance from root. Pick the greatest; ties break on the most recently created,
        // which Toolhelp does not give us, so on the last one enumerated.
        int Depth(Entry e)
        {
            int d = 0;
            var cur = e;
            while (cur.Pid != rootPid && d < 64)
            {
                var parent = all.FirstOrDefault(p => p.Pid == cur.ParentPid);
                if (parent.Pid == 0) break;
                cur = parent;
                d++;
            }
            return d;
        }
        return kids.OrderByDescending(Depth).ThenByDescending(k => k.Pid).First();
    }

    public static string Describe(int rootPid)
    {
        var all = Snapshot();
        var kids = Descendants(rootPid, all);
        var self = all.FirstOrDefault(p => p.Pid == rootPid, new Entry(rootPid, 0, "?"));
        if (kids.Count == 0) return self.Exe + "(" + self.Pid + ")";
        return self.Exe + "(" + self.Pid + ") -> " + string.Join(", ", kids.Select(k => k.Exe + "(" + k.Pid + ")"));
    }
}
