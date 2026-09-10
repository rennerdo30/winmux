# ADR 0003 — Foreign-app embedding: what "any Windows app" actually means

- **Status:** accepted
- **Date:** 2026-09-10
- **Spike:** [`spikes/02-reparent`](../../spikes/02-reparent) (Phase 0, spike 2)
- **Seeds:** [`spikes/02-reparent/quirks-seed.json`](../../spikes/02-reparent/quirks-seed.json)

## Context

The README claims WinMux can host "*any* Windows application", hedged with a list of known-hard
cases. CLAUDE.md §7 spike 2 exists to turn that claim into a measured compatibility surface and
to seed the quirks database.

Measured on Windows 10.0.26200, .NET 10, x64, non-elevated host, PerMonitorV2 +
`SetThreadDpiHostingBehavior(MIXED)`. Each target is launched, its main window located by the
adoption rule in CLAUDE.md §5, reparented into a borderless host, resized to three sizes, moved,
then detached with an exact restore check.

## Findings

| target | window class | awareness | find | verdict |
|---|---|---|---|---|
| Notepad (packaged, via shim) | `Notepad` | PerMonitor | 410 ms | **OK** |
| Character Map (classic Win32) | `#32770` | PerMonitor | 404 ms | **OK** |
| Explorer | `CabinetWClass` | PerMonitor | 736 ms | **OK** |
| VS Code (Chromium) | `Chrome_WidgetWin_1` | PerMonitor | 405 ms | **OK** |
| Guinea pig (DPI-**unaware**) | `WinMuxGuineaPig` | Unaware | 403 ms | **OK**, 1px width error |
| Guinea pig (system-aware) | `WinMuxGuineaPig` | System | 404 ms | **OK** |
| Task Manager | `TaskManagerWindow` | System | 650 ms | **EMBED FAILED** — UIPI |
| Calculator (packaged/UWP) | `ApplicationFrameWindow` | PerMonitor | 406 ms | **EMBED FAILED** |

"OK" means: embedded, followed all three resizes and a move to within tolerance, detached, and
restored parent, style, ex-style and rect **byte-exactly**, with the app still alive and visible.

**1. Ordinary apps embed cleanly, including Chromium and the shell.** Explorer and VS Code — the
two most likely to fight back — behaved identically to Character Map. Detach was byte-exact in
every successful case, which is what makes the operation reversible (CLAUDE.md §5).

**2. The two failures have distinct, detectable causes, and neither is silent.**
- **Task Manager: `SetParent` → `ERROR_ACCESS_DENIED` (5).** This is UIPI, exactly as §5 predicts.
  Note it is *higher integrity*, not elevated in the usual sense: `OpenProcess` for query
  **succeeds** (we read its DPI awareness), so probing the process is not a valid UIPI test.
  Only the error from `SetParent` itself is.
- **Calculator: `SetParent` → `ERROR_INVALID_PARAMETER` (87).** The `ApplicationFrameWindow` simply
  refuses to be reparented. This is a hard refusal, not a race or a timing problem.

**3. Detecting refusal requires reading the error immediately.** `SetParent` returning the old
parent is not success — a failed call returns `NULL` and sets the error, and any subsequent Win32
call clobbers it. Capture `GetLastError` on the very next line, then verify by reading the parent
back.

**4. `GetParent` is the wrong API for verification.** For a `WS_POPUP` window it returns the
**owner**, not the parent. Calculator's window has style `0x94CF0000` (`WS_POPUP` set), so
`GetParent` reported an unrelated handle. Use `GetAncestor(hwnd, GA_PARENT)`, which returns the
desktop for a genuine top-level window.

**5. `notepad.exe` on Windows 11 is a shim.** It launches the packaged `Notepad.exe` as a
*different process*, so matching windows by the launched pid never finds it. Any window-selection
rule that assumes "the window belongs to the process I started" is wrong for a whole class of
modern apps. Matching by process image name works.

**6. The adoption rule in CLAUDE.md §5 is correct and load-bearing.** Notepad alone creates **12**
top-level windows within 9 s: `GDI+ Hook Window Class`, three `IME`, two `MSCTFIME UI`,
`tooltips_class32`, `CtrlNotifySink` and others, most of them invisible or owned. Filtering to
visible + top-level + non-owned + has-title + larger than 120×80 selected the right window every
time, on every target.

**7. DPI-awareness mismatch works, with a rounding artifact.** A deliberately DPI-**unaware**
window hosted in a PerMonitorV2 host embedded, resized and detached correctly under mixed-mode
hosting — but its width came back **one pixel larger than requested** (asked 700, got 701; asked
880, got 881), consistently, from DPI virtualization rounding. Harmless at 150%, but layout code
must not assume a pane's actual rect equals its requested rect.

## Decision

1. **Ship embed as the default and attach as the documented fallback**, with the per-app choice in
   the quirks database, as §5 already requires. The measured surface justifies the claim in the
   README provided the exceptions stay listed.

2. **Window selection matches on process image name and window class, not launched pid.** The pid
   is a hint, not a rule. `spikes/02-reparent/quirks-seed.json` is the seed format:
   match (exe + class) → strategy, window-selection rule, launch delay, limitations, verified-on.

3. **Refusal detection is: capture `GetLastError` immediately after `SetParent`, then verify with
   `GetAncestor(GA_PARENT)`.** Map error 5 to a plain-words UIPI message and error 87 to "this app
   refuses embedding"; both fall back to attach mode automatically. Never report a refusal as a
   generic failure — §8 forbids silent failure here.

4. **UWP/packaged apps default to attach mode.** Calculator's hard refusal confirms the README.
   Do not spend more time trying to reparent `ApplicationFrameWindow`.

5. **Layout must tolerate a pane whose actual rect differs from its requested rect.** Both from
   DPI rounding (finding 7) and from apps with minimum sizes. Never assert equality.

## Consequences

- The compatibility claim is now evidence-backed for five real applications plus two synthetic
  awareness levels, and the two failures are explained rather than merely observed.
- The quirks database has a concrete schema and a first set of verified entries.
- Attach mode is now load-bearing for at least UWP and higher-integrity apps, which raises the
  priority of making attach mode good — and ADR 0001 already showed it is *not* the safer option
  against a wedged app.

## What failed

- **True mixed-DPI multi-monitor was NOT tested.** Both monitors on this machine run at 144 DPI
  (150%), so the scenario CLAUDE.md calls "where the bugs live" — dragging a hosted app between
  monitors of *different* scale — remains unmeasured. The DPI-awareness axis was covered instead.
  This needs a human to set one display to a different scaling factor. **Open.**
- **The first DPI test was vacuous and scored OK.** Launching Character Map with
  `__COMPAT_LAYER=DPIUNAWARE` did not change its awareness at all — the app's manifest wins — so
  the row reported OK while testing nothing. Replaced with a guinea-pig process whose awareness we
  set ourselves; the target was deleted rather than left in the table.
- **The guinea pig then failed the same way for a second reason.** `SetProcessDpiAwarenessContext`
  returned false because the *spike's own* `app.manifest` declared PerMonitorV2, and a
  manifest-declared awareness cannot be overridden at runtime. The manifest was removed and the
  host now sets its awareness in code. Only after both fixes did the process actually report
  `Unaware`.
- **The first UIPI test measured the wrong window.** A loose "any new window" match grabbed a
  stray Chrome window titled *"Task Manager - Google Chrome"* after 16.5 s and scored it OK.
  Fixed by matching on process image name (`Taskmgr.exe`).
- **The first Calculator and Task Manager failures were reported as "last error 0".** `Embed` made
  three more Win32 calls between `SetParent` and reading the error. The real codes — 87 and 5 —
  only appeared once the error was captured on the next line, and they are the whole finding.
- **The first Notepad run reported "no adoptable window" for 25 s**, which looked like a platform
  limitation and was actually the shim-pid problem (finding 5). A probe mode that dumps every new
  top-level window found it in one run; guessing at it did not.
