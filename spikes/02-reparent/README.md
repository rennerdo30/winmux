# Spike 2 — reparenting real applications

Phase 0, spike 2. Answers: *what does "any Windows app" actually mean?*

Result and decision: [ADR 0003](../../docs/adr/0003-foreign-app-compatibility.md).
Machine-readable output: [`quirks-seed.json`](quirks-seed.json).

## Running it

```powershell
dotnet build -c Release
$exe = ".\bin\Release\net10.0-windows\ReparentSpike.exe"

& $exe                                   # full matrix, ~1 minute
& $exe --only Explorer                   # one target
& $exe --nomixed                         # without SetThreadDpiHostingBehavior(MIXED)
& $exe --probe "C:\WINDOWS\system32\notepad.exe" --watchms 9000
```

Windows open and close on your desktop while it runs. Each target is launched, located, embedded
into a borderless host, resized to three sizes, moved, then detached and checked for an exact
restore.

`--probe` is the diagnostic that earns its keep: it dumps **every** new top-level window an app
creates — visible or not, owned or not, any process — with class, title, pid, exe, styles and DPI
awareness. Notepad creates 12. Use it before writing a window-selection rule, not after.

## What it found

| target | verdict |
|---|---|
| Notepad (packaged, via shim) | OK |
| Character Map (classic Win32) | OK |
| Explorer | OK |
| VS Code (Chromium) | OK |
| Guinea pig, DPI-unaware / system-aware | OK (1px width error when unaware) |
| Task Manager | refused — `ERROR_ACCESS_DENIED` (UIPI) |
| Calculator (packaged/UWP) | refused — `ERROR_INVALID_PARAMETER` |

Full analysis in ADR 0003. Three traps worth knowing before touching this code:

- **`GetParent` returns the OWNER for a `WS_POPUP` window**, not the parent. Verify with
  `GetAncestor(hwnd, GA_PARENT)`.
- **Capture `GetLastError` on the line after `SetParent`.** Anything else clobbers it, and the
  error code is the entire diagnosis (5 = UIPI, 87 = the window refuses).
- **`notepad.exe` on Win11 is a shim** — the window belongs to a different pid than the one you
  launched. Match on process image name.

## Known limits of this harness

- **True mixed-DPI multi-monitor is NOT tested.** Both monitors here are 144 DPI, so the case
  CLAUDE.md calls "where the bugs live" is unmeasured. Needs a human to set one display to a
  different scale factor, then re-run. The DPI *awareness* axis is covered by the guinea pig.
- The host is a single process, not `WinMux.PaneHost`. Hang safety is spike 3's subject, not this
  one; this spike's window calls run on a worker thread and would block against a wedged app.
- Nine targets on one machine, one Windows build, one session. The quirks database is a surface
  that grows; this is a seed, not coverage.
- No adoption of *already-running* windows, no multi-window apps, no tray-only apps, no installers.

Throwaway code, per CLAUDE.md §7 — except `quirks-seed.json`, which is the deliverable.
