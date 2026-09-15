# HANDOFF — WinMux

## Where we are

**Phase 4 is complete:** pane types are real providers behind the public `WinMux.Panes` contract,
and the built-in file browser ships alongside terminal and foreign-app panes. A fourth kind needs
no change to Core, the layout tree, persistence or the CLI. See
[ADR 0012](docs/adr/0012-phase-4-pane-providers-and-file-browser.md).

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`.
Re-verified 2026-09-15: **zero build warnings; 280/280 tests passing**. Visible acceptance is
`scripts/phase4-demo.ps1` (needs **pwsh 7**, not Windows PowerShell 5.1); `-VerifyOnly` runs the
measured checks without the long-running walkthrough.

## What just happened

**2026-09-13** — Codex implemented Phase 4. **2026-09-15** — re-verified and committed.

- `PaneKind` became a validated, case-normalized string instead of a closed enum. The enum made
  providers extensible in name only: a provider in another assembly could not define a kind.
  Unknown kinds now round-trip through TOML rather than being destroyed when no provider is present.
- New public `WinMux.Panes` assembly owns `IPaneProvider`, `IPaneRuntime`, provider context and
  registry. It references Core and Avalonia and nothing platform-specific — enforced by
  `Phase4PaneProviderBoundaryTests`.
- Terminal, foreign-app and file-browser are now providers. `MainWindow` keeps only layout, action
  routing, chrome and generic runtime coordination. A missing provider or failed restore produces a
  visible placeholder that retains the original descriptor.
- The file browser is a trusted **in-process** Avalonia runtime — the documented exception to "a
  pane is a process". Enumerates off the UI thread, cancels superseded work, persists current
  directory and selection, and falls back visibly when a restored directory is gone.
- `new-file-browser` and `open-terminal-here` are named actions in keymap, palette and CLI
  (`winmux files`, `winmux terminal-here`); CLI dispatch awaits real completion.

**Why it sat uncommitted for two days:** the Codex session hit ~99% of its weekly rate limit right
after the final verification run, so it never committed or updated this file. The work itself was
finished and passing.

## The next action

**Start Phase 5: extract `WinMux.Platform`.** Define `IWindowHost` and friends, move the Win32
operations in `WinMux.Platform.Win32`, `WinMux.PaneHost` and the shell behind it, and keep the
provider and layout contracts unchanged (ADR 0012 was written to make this possible). The point is
the discipline, not an X11 port: if the seam is right, the portable half stays portable.

## Blocked / needs a human

- **Mixed-scale multi-monitor remains unmeasured.** Both monitors are 144 DPI. PaneHost opts into
  PerMonitorV2/mixed hosting, but someone must set different display scales and run the Phase 3
  walkthrough across them before fidelity is claimed.
- **Phase 4's interactive visual pass is still a human walkthrough.** The automation bridge exposed
  browser tabs but no native windows, so no visual result was inferred from it. Run
  `scripts/phase4-demo.ps1` under pwsh 7 and look.
- **Higher-integrity attach remains constrained by UIPI.** Task Manager is selected for attach and
  failures are visible, but positioning may still be refused; WinMux will not elevate.
- **Input-queue starvation remains unmeasured.** Keep PaneHost top-level; never reparent it into
  the shell.
- **Supply-chain call before v1:** `Terminal.Emulation` 0.3.3 has no public source. The owned seam
  bounds replacement cost but does not answer whether to ship it.

## Do not re-do

- Do not hand-roll ConPTY; see [ADR 0002](docs/adr/0002-terminal-stack.md).
- Do not hand-roll `SetParent` into an Avalonia window — it succeeds, reports the right parent and
  rect, and paints nothing; see [ADR 0007](docs/adr/0007-hosting-foreign-windows.md).
- Do not rely on Porta.Pty's Windows quoting or rank every OSC report over a newer deepest-child
  PEB; both measured counterexamples are in [ADR 0010](docs/adr/0010-phase-2-persistence-runtime.md).
- Do not call a foreign HWND from the UI thread or hard-kill a host that still owns a child; see
  [ADR 0008](docs/adr/0008-pane-host-ipc.md) and [ADR 0011](docs/adr/0011-phase-3-foreign-app-runtime.md).
- Do not pass a lifetime token where an operation token belongs: the first file-browser navigation
  cancelled scheduling but let superseded directory reads continue (ADR 0012).
- Do not let action dispatch return before async pane creation completes, or the CLI reports
  success for a later failure (ADR 0012).
- Do not run `scripts/phase*-demo.ps1` under Windows PowerShell 5.1; the verifiers need pwsh 7.
- Do not assert exact requested rectangles for arbitrary apps; DPI rounding and minimum sizes are
  measured counterexamples.
- Do not put palette/modal chrome over the pane canvas; native windows paint above it.
- `E:\Development\winmux` and `D:\Development\winmux` are the same project via subst/junction.
