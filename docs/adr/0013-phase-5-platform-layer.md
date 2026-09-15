# ADR 0013 — Phase 5 platform layer

- **Status:** accepted
- **Date:** 2026-09-15
- **Code:** `WinMux.Platform`, `WinMux.Platform.Win32/Windows`, `WinMux.Shell`
- **Acceptance:** `dotnet test WinMux.slnx -c Release` — 315 passed, 0 warnings

## Context

Priority 6 calls cross-platform "a design discipline (keep the core portable), not a shipping
commitment". Through Phase 4 that discipline existed only at the Core boundary, which
`CoreIsPlatformFreeTests` enforces. Everything above Core was free to call Windows directly, and did:

- `WinMux.Shell/Win32Interop.cs` held 24 `DllImport`s. **Eighteen of them, and every `WS_*`/`GWL_*`
  constant, were dead** — left over from the Phase 1 hand-rolled `SetParent` attempt that ADR 0007
  and ADR 0008 replaced. The file read like a window-management library the shell did not have.
- `ForeignWindowTracker` mixed two unrelated things: a placement *policy* (deduplicate, re-assert on
  activation, nudge once on first placement) that is not Windows-specific at all, and the
  `SetWindowPos`/`ShowWindow`/`RedrawWindow` calls that carry it out.
- `ProcessWorkingDirectoryResolver` was already split internally — policy plus a private
  `NativeMethods` — but the seam was a language feature, not a testable boundary. Every rule spike 4
  paid for (skip console infrastructure, prefer the deepest descendant, tie-break by PID) could only
  be exercised against whatever happened to be running on the test machine.

The cost of that was not portability, which nobody is buying yet. It was that the decisions worth
testing were welded to calls that cannot be tested.

## Decision

1. **`WinMux.Platform` is the shape of an operating system, and nothing else.** It targets
   `net10.0` — deliberately not `net10.0-windows` — references only `WinMux.Core`, and contains no
   P/Invoke. Three interfaces and the values they speak in:
   - `WindowHandle` — an opaque integer handle. Every windowing system has one; `HWND` is a name
     for the Windows one, and that name does not appear above the implementation.
   - `IHostWindowService` + `WindowPlacement`/`WindowPlacementMode` — where a hosted window should
     be, stated as intent. The contract says the destination; the implementation owns whatever its
     platform demands to arrive there. It has no close or destroy method: ADR 0008 routes that
     through the PaneHost protocol, because the shell must never call a foreign HWND at all.
   - `IProcessInspector` + `ProcessSnapshotEntry` — the process facts strategy 2 of the cwd capture
     needs.
   - `IUserNotifier` — saying something when startup fails before a window exists.

2. **`WinMux.Platform.Win32/Windows/` implements them.** `Win32HostWindowService`,
   `Win32ProcessInspector` and `Win32UserNotifier` carry every call the shell used to make, ported
   unchanged — including the four measured workarounds (`SWP_NOCOPYBITS`, the off-by-one resize on
   first placement, the hide/show cycle for DirectComposition content, `RedrawWindow` with
   `RDW_ALLCHILDREN`) and the exact refusal wording from ADR 0004.

3. **The shell keeps the policy and loses the platform.** `ForeignWindowTracker` still owns its
   thread, its dedup and its first-placement bookkeeping, and now hands a `WindowPlacement` to the
   interface. `ProcessWorkingDirectoryResolver` becomes an ordinary class taking an
   `IProcessInspector`. `Win32Interop.cs` is deleted rather than ported — the eighteen dead imports
   were never part of the design. **`WinMux.Shell` now declares zero P/Invoke.**

4. **`WinMux.Shell/PlatformServices.cs` is the single composition root** and the only file in the
   shell that names `WinMux.Platform.Win32.Windows`. Its being *single* is asserted, not intended.

5. **`WinMux.PaneHost` keeps its 35 imports, on purpose.** Its entire job is Win32 reparenting:
   style surgery, `SetParent`, byte-exact restore, a message pump. That is not an operation to put
   behind an interface — it *is* a platform implementation that happens to be an executable, for the
   reason ADR 0001 gave (the shell must never make a synchronous call against a foreign window, so
   the implementation needs its own process). An X11 port needs a different host binary, not the
   same one with a shim. Stated here so a later session does not read the omission as unfinished.

6. **Enforced by test.** `PlatformBoundaryTests` asserts the framework has no OS suffix, that the
   declared dependencies are Core alone, that no P/Invoke and no OS-shaped type name exists in the
   contract, that the shell declares no P/Invoke, and that exactly one shell file names a concrete
   OS. `Rect` keeps whole physical pixels; Avalonia's device-independent `Rect` is aliased at the
   one call site where both appear, so the two can never be confused silently.

## Consequences

- The placement policy and the cwd policy now have real tests — 19 of them, against fakes, with no
  window and no live process tree. That is the return on the extraction, and it arrived immediately:
  two behaviours were wrong in ways nobody could have seen before. A first placement made while the
  pane was *hidden* spent the one-time first-placement licence, so an inactive tab never got its
  adoption nudge when it was finally shown; and a placement that threw mid-call consumed the licence
  too. Both are fixed and both are now covered.
- Adding a second platform is now a matter of implementing three interfaces plus a host executable,
  rather than auditing the shell for calls.
- The shell still references `WinMux.Platform.Win32` for the quirks *database* — a rules table, not
  an OS call, and ADR 0011 already put the shell in charge of resolving it. The boundary test bans
  the `.Windows` namespace specifically rather than the whole assembly.
- A first draft of the contract carried a `RequestClose`, which nothing called. It was removed
  before commit: an unused lifetime method is the same mistake as the eighteen dead imports, only
  newer.
- `Win32HostWindowService` is not unit-tested beyond "a dead handle is never touched". Everything
  else it does is a synchronous cross-process call, which is exactly what ADR 0001 forbids a test
  from waiting on. The human walkthrough in HANDOFF.md remains the verification, and saying so is
  better than a test that fakes the OS and proves nothing.

## What failed

- **The boundary test that could not fail, again.** `Platform_references_no_platform_assembly`
  passed happily with `System.Drawing.Common` added to the project file, for the same reason
  `CoreIsPlatformFreeTests` once did: `GetReferencedAssemblies()` reports what the compiler emitted,
  not what was declared. Only the project-file assertion catches it. Every guard in this ADR was
  therefore mutation-tested before being trusted — a `DllImport` added to the shell, one added to
  the contract, a second `using WinMux.Platform.Win32.Windows`, an unused platform package, and an
  OS-suffixed framework. All five failed the suite; the fifth failed harder than expected, at
  NuGet restore, because the guard project targets `net10.0` and so physically cannot reference an
  OS-specific contract. That is a better enforcement mechanism than the test.
- **`Rect` is ambiguous in the shell.** Avalonia has one of its own, in device-independent doubles,
  and the pane provider imports both namespaces — so an unqualified `Rect` there silently means the
  wrong thing at the exact boundary where whole physical pixels start to matter. Caught while
  writing rather than while debugging, and aliased so it stays caught.
- **Porting `Win32Interop.cs` wholesale was the obvious move and the wrong one.** Reading it for
  the port is what revealed that 18 of its 24 imports had no callers. The extraction was worth
  roughly as much as deletion here.
