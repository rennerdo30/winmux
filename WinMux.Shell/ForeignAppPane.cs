using System.Diagnostics;
using WinMux.Core.Model;

namespace WinMux.Shell;

/// <summary>
/// Launches a foreign application and locates the window that represents it.
///
/// Window selection follows spike 2 / ADR 0003: match on process image name or window class, never
/// on the launched pid — on Windows 11 `notepad.exe` is a shim, and `explorer.exe` exits the
/// instant it hands off to the running shell. A quirks entry carries the rule per app.
/// </summary>
internal sealed class ForeignAppPane
{
    public PaneId Id { get; }
    public Pane Pane { get; }
    public IntPtr Hwnd { get; private set; }
    public string Status { get; private set; } = "starting…";
    public bool Located => Hwnd != IntPtr.Zero && Win32Interop.IsWindow(Hwnd);

    private Process? _process;
    private EmbedRecord? _embed;

    /// <summary>True once the window has been prepared for hosting.</summary>
    public bool IsEmbedded => _embed is not null;

    public ForeignAppPane(Pane pane) { Pane = pane; Id = pane.Id; }

    public async Task LaunchAsync(ISet<IntPtr> claimed, CancellationToken token)
    {
        var r = Pane.Restore;
        if (string.IsNullOrWhiteSpace(r.Program)) { Status = "no program set"; return; }

        var before = Win32Interop.AdoptableWindows().ToHashSet();

        var psi = new ProcessStartInfo(r.Program) { UseShellExecute = false };
        foreach (var a in r.Args) psi.ArgumentList.Add(a);
        if (r.Cwd.IsKnown && Directory.Exists(r.Cwd.Path)) psi.WorkingDirectory = r.Cwd.Path;
        foreach (var (k, v) in r.EnvOverrides) psi.Environment[k] = v;

        try { _process = Process.Start(psi); }
        catch (Exception ex) { Status = "could not start: " + ex.Message; return; }

        var expectedClass = r.Extras.GetValueOrDefault("window_class");
        var expectedProcess = r.Extras.GetValueOrDefault("process_name")
                              ?? Path.GetFileName(r.Program);
        var settle = int.TryParse(r.Extras.GetValueOrDefault("launch_delay_ms"), out var d) ? d : 1200;

        Status = "waiting for a window…";
        var found = await Task.Run(
            () => WaitForWindow(before, claimed, expectedClass, expectedProcess, settle, token), token);

        if (found == IntPtr.Zero)
        {
            Status = "no window appeared within 20s";
            return;
        }
        lock (claimed) claimed.Add(found);

        Hwnd = found;
        Status = Win32Interop.GetClassName(found);
        if (Win32Interop.IsIconic(found)) Win32Interop.ShowWindow(found, Win32Interop.SW_RESTORE);
    }

    /// <summary>
    /// Record the original state and strip the window's frame, ready for Avalonia to reparent it.
    ///
    /// `SetParent` does not fix styles (CLAUDE.md section 5): leave the caption and border bits
    /// alone and the app keeps its own title bar inside the pane, which is the visible difference
    /// between a window that is embedded and one that is merely being followed.
    /// </summary>
    public void StripFrame()
    {
        if (!Located || _embed is not null) return;

        Win32Interop.GetWindowRect(Hwnd, out var originalRect);
        _embed = new EmbedRecord(
            Hwnd,
            Win32Interop.GetAncestor(Hwnd, Win32Interop.GA_PARENT),
            Win32Interop.GetWindowLongPtrW(Hwnd, Win32Interop.GWL_STYLE),
            Win32Interop.GetWindowLongPtrW(Hwnd, Win32Interop.GWL_EXSTYLE),
            originalRect);

        Embedding.StripFrame(Hwnd, _embed);
    }

    /// <summary>
    /// Put the window back exactly as it was. MUST run before the shell window is destroyed:
    /// a parent taking its children down with it is how spike 3 lost an application.
    /// </summary>
    public void Detach()
    {
        if (_embed is null) return;
        Embedding.Detach(_embed);
        _embed = null;
    }

    private static IntPtr WaitForWindow(HashSet<IntPtr> before, ISet<IntPtr> claimed,
                                        string? expectedClass, string expectedProcess,
                                        int settleMs, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var candidates = new List<IntPtr>();

        bool Matches(IntPtr h) =>
            !string.IsNullOrEmpty(expectedClass)
                ? Win32Interop.GetClassName(h).Equals(expectedClass, StringComparison.OrdinalIgnoreCase)
                : Win32Interop.GetProcessImageName(h).Equals(expectedProcess, StringComparison.OrdinalIgnoreCase);

        bool Unclaimed(IntPtr h) { lock (claimed) return !claimed.Contains(h); }

        // First choice: a window that appeared because we launched the app.
        while (sw.ElapsedMilliseconds < NewWindowTimeoutMs && !token.IsCancellationRequested)
        {
            foreach (var h in Win32Interop.AdoptableWindows())
            {
                if (before.Contains(h) || candidates.Contains(h)) continue;
                if (!Matches(h) || !Unclaimed(h)) continue;
                candidates.Add(h);
            }

            // Let splash screens resolve into the real window rather than grabbing the first thing.
            if (candidates.Count > 0 && sw.ElapsedMilliseconds >= settleMs) return candidates[^1];
            Thread.Sleep(100);
        }

        // Fallback: adopt an existing unclaimed window that matches.
        //
        // Some applications REUSE a window instead of opening one. `explorer.exe <folder>` with a
        // window already open on that folder simply activates it, so nothing new ever appears and
        // waiting for a new window waits forever. Observed on the second run of this very app.
        foreach (var h in Win32Interop.AdoptableWindows())
            if (Matches(h) && Unclaimed(h)) return h;

        return IntPtr.Zero;
    }

    /// <summary>How long to hold out for a freshly created window before adopting an existing one.</summary>
    private const int NewWindowTimeoutMs = 8000;

    public void Close()
    {
        // Detach BEFORE closing: destroying the host while it still owns the child destroys the
        // child too, and the application is left running with no window at all.
        Detach();
        if (Located) Win32Interop.PostMessageW(Hwnd, Win32Interop.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        try { if (_process is { HasExited: false }) _process.CloseMainWindow(); }
        catch (InvalidOperationException) { }
        Hwnd = IntPtr.Zero;
    }
}
