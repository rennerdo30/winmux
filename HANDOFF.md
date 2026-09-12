# HANDOFF — WinMux

## Where we are

**Phase 3 is complete:** WinMux persistently hosts terminals plus foreign applications through one
top-level out-of-process PaneHost per app, with measured automatic embed/attach selection, clean
fallback, and live strategy switching. See [ADR 0011](docs/adr/0011-phase-3-foreign-app-runtime.md).

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`.
Current result: **zero build warnings; 239/239 tests passing**. Visible acceptance is
`scripts/phase3-demo.ps1`; `-VerifyOnly` runs the measured desktop checks without opening the
long-running three-pane walkthrough.

## What just happened

**2026-09-12** — Phase 3 closed.

- Added strict, versioned quirks loading and specificity matching in `WinMux.Platform.Win32`.
  The measured seed ships as editable `foreign-app-quirks.json`; executable/launcher, class,
  optional title, selection mode, settle delay, limitations, and verification evidence survive validation.
- Added persisted `strategy = 'auto'`. The shell resolves quirks without touching a foreign HWND,
  passes explicit host arguments, reports malformed data, and remembers effective manual/fallback
  choices.
- PaneHost now implements embed, attach, same-HWND automatic fallback, and live switching. Embed
  uses correct child/popup/frame styles and verifies the true parent; attach uses async positioning.
  The host is PerMonitorV2 with mixed hosting enabled.
- Hardened lifecycle behavior: redirected-input loss detaches autonomously, destroy restores as a
  final guard, restoration is verified before host destruction, and attached windows follow pane
  visibility. Shell/pane close waits for `DETACHED`/`CLOSED` and stays open on failure; it never
  hard-kills a host that may own an embedded child.
- Measured Character Map embed/attach/live switching and packaged Calculator error-87 fallback.
  Both followed the host and survived detach; the Calculator limitation is surfaced in plain words.

## The next action

**Start Phase 4:** define the public pane-provider interface, then implement the built-in file
browser pane with persisted current directory/selection and terminal handoff.

## Blocked / needs a human

- **Mixed-scale multi-monitor remains unmeasured.** Both available monitors are 144 DPI. PaneHost
  now opts into PerMonitorV2/mixed hosting, but a human must set different display scales and run
  the Phase 3 walkthrough across them before claiming fidelity.
- **Higher-integrity attach remains constrained by UIPI.** Task Manager is selected for attach and
  failures are visible, but useful positioning may still be refused; WinMux will not elevate.
- **Input-queue starvation remains unmeasured.** Keep PaneHost top-level; never reparent it into
  the shell.
- **Supply-chain call before v1:** `Terminal.Emulation` 0.3.3 has no public source. The owned seam
  bounds replacement cost but does not answer whether to ship it.

## Do not re-do

- Do not hand-roll ConPTY; see [ADR 0002](docs/adr/0002-terminal-stack.md).
- Do not rely on Porta.Pty's Windows quoting or rank every OSC report over newer deepest-child PEB;
  both measured counterexamples are recorded in [ADR 0010](docs/adr/0010-phase-2-persistence-runtime.md).
- Do not call a foreign HWND from the UI thread or hard-kill a host that still owns a child; see
  [ADR 0008](docs/adr/0008-pane-host-ipc.md) and [ADR 0011](docs/adr/0011-phase-3-foreign-app-runtime.md).
- Do not assert exact requested rectangles for arbitrary apps; DPI rounding and minimum sizes are
  measured counterexamples.
- Do not put palette/modal chrome over the pane canvas; native windows paint above it.
- `E:\Development\winmux` and `D:\Development\winmux` are the same project via subst/junction.
