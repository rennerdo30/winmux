# HANDOFF — WinMux

## Where we are

**Phase 6 (GUI) has started and its first slice is done:** tab groups nest anywhere in the tree,
each with its own strip on any edge, and every layout operation is a toolbar button as well as a
key. The tab strip is reserved geometry emitted by the layout engine, which is what keeps it from
being painted over by a foreign app's window. See
[ADR 0014](docs/adr/0014-nested-tab-groups-and-shell-chrome.md).

**Phase 5 is complete:** the platform layer is extracted. `WinMux.Platform` (net10.0, Core-only,
no P/Invoke) states what an operating system must provide — `IHostWindowService`,
`IProcessInspector`, `IUserNotifier`, `WindowHandle` — and `WinMux.Platform.Win32/Windows/`
provides it. **`WinMux.Shell` now declares zero `DllImport`**, and the placement and cwd policies
it kept are unit-tested against fakes for the first time. See
[ADR 0013](docs/adr/0013-phase-5-platform-layer.md). Phases 1–4 are unchanged; the provider and
layout contracts did not move.

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`.
Verified 2026-09-15: **zero build warnings; 344/344 tests passing**.
Run it with `scripts/run.ps1`; `scripts/publish.ps1` produces a copyable `dist/Release`. Visible acceptance is still
`scripts/phase4-demo.ps1` (needs **pwsh 7**, not Windows PowerShell 5.1); `-VerifyOnly` runs the
measured checks without the long-running walkthrough.

## What just happened

**2026-09-15** — Phase 5, the platform extraction ([ADR 0013](docs/adr/0013-phase-5-platform-layer.md)).

- `WinMux.Platform` is new: three interfaces and the values they speak in. It targets `net10.0`
  with no OS suffix on purpose, which turns out to be self-enforcing — the guard project also
  targets `net10.0`, so an OS-specific contract fails at NuGet restore, before any test runs.
- `WinMux.Platform.Win32/Windows/` holds `Win32HostWindowService`, `Win32ProcessInspector` and
  `Win32UserNotifier`, carrying every call the shell used to make, including all four measured
  repaint workarounds and ADR 0004's exact refusal wording.
- `WinMux.Shell/Win32Interop.cs` was **deleted, not ported**: 18 of its 24 imports had no callers
  at all, left over from the Phase 1 hand-rolled `SetParent` attempt. `ForeignWindowTracker` keeps
  its thread and its policy; `ProcessWorkingDirectoryResolver` takes an `IProcessInspector`;
  `PlatformServices.cs` is the single composition root.
- **Two real bugs fell out of writing the tests**, both invisible before: a placement made while
  the pane was hidden spent the one-time first-placement licence, so an inactive tab never got its
  adoption nudge when shown; and a placement that threw mid-call consumed it too.
- `PlatformBoundaryTests` enforces the seam. Every guard was mutation-tested before being trusted,
  because `Platform_references_no_platform_assembly` alone would have passed an unused platform
  package — the same blind spot `CoreIsPlatformFreeTests` once had.

**Also 2026-09-15** — Phase 6's first slice
([ADR 0014](docs/adr/0014-nested-tab-groups-and-shell-chrome.md)).

- `StackNode` carries a `TabStrip` placement (top/bottom/left/right), persisted, defaulting to top
  and omitted from the file when it is the default. Old sessions load unchanged.
- `Layouter` reserves the strip out of the stack's rectangle and reports it on the `Arrangement`.
  One strip per stack, wherever the stack is — the old single top bar could only ever describe one.
- A toolbar, per-stack strips with `+`/close/placement menus, and a centralised `Chrome/Theme.cs`.
- The chrome now takes its light/dark variant and its accent from Windows, renders on a **Mica**
  backdrop (verified by reading back `ActualTransparencyLevel`), and draws its icons as geometry
  rather than trusting a symbol font to exist. See the addendum to ADR 0014.
- `scripts/run.ps1`, `scripts/publish.ps1` (to `dist/`), and `examples/tabs-and-splits.toml`.
- **A shipped build bug, found while producing a testable exe:** the shell copied PaneHost from
  `bin/$(Configuration)` while PaneHost, being x64-only, builds to `bin/x64/$(Configuration)`.
  It had been shipping a three-day-old artifact; on a clean clone it would have copied nothing,
  silently. Now resolved via `GetTargetPath`, and the build fails if there is nothing to copy.

**Earlier: 2026-09-13** — Codex implemented Phase 4; re-verified and committed 2026-09-15.

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

**Look at it, with a foreign app in a tab group.** `scripts/run.ps1 -Session examples/cmd-and-explorer.toml`,
then Ctrl+B c to tab the Explorer pane and move the strip to the left. Two things can only be
checked by eye: that Explorer still paints its own content after Phase 5 rerouted every placement
call, and that a tab strip beside a native pane is not painted over by it. The layout guarantees
they do not overlap; whether Windows agrees is the open question.

After that, the roadmap's open items are unchanged: provider discovery and packaging, tab and pane
**reordering** (the strips are the surface it attaches to), foreign-window focus reconciliation,
terminal selection and scrollback navigation — the last is the sharpest gap, because the engine
keeps scrollback and can address it while the control has no wheel handler at all.

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
- **An X11 port needs its own host executable, not a shim.** `WinMux.PaneHost` keeps its 35
  imports deliberately (ADR 0013, decision 5): reparenting *is* a platform implementation, and
  ADR 0001 is why it needs its own process. Do not read that omission as unfinished Phase 5 work.
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
- Do not add a method to `WinMux.Platform` before something calls it, and do not write `Rect`
  unqualified in a file that imports Avalonia — it has one of its own, in device-independent
  doubles (ADR 0013). The same is true of `TabStrip` (ADR 0014): assume any short geometric or
  widget-shaped name in Core has an Avalonia namesake.
- Do not set `Background` directly on a Button to style it. It replaces the value Fluent's template
  animates, so the control loses hover and pressed feedback and reads as broken (ADR 0014). Style
  the templated `ContentPresenter` instead.
- Do not reuse the horizontal tab-strip thickness for a vertical strip; 28px of titles is a column
  of ellipses (ADR 0014). `LayoutMetrics` carries both.
- Do not guess another project's output path in a copy target. PaneHost is x64-only and builds to
  `bin/x64/...`; ask MSBuild with `GetTargetPath`.
- Do not `dotnet build` WinMux.Shell.csproj alone and then run `bin/x64/Release/...`. The project
  is x64 only through the solution mapping, so a bare project build lands in `bin/Release/` and you
  test a stale exe. Build the solution (ADR 0014 addendum).
- Do not put `SnapshotProcesses` on an interactive path. It costs 123 ms for ~330 processes while
  the working-directory read it enables costs 0.12 ms; `CachedProcessInspector` now shares one list
  across panes and refreshes in the background. Eight panes cost 821 ms per capture pass without it.
- Do not call `SessionController.RequestSave()` from anything that fires at input frequency
  without checking what capture costs. Debouncing the *write* does not help when producing the
  snapshot is the expensive part: it walks every process on the machine per terminal pane
  (120 ms for 334 processes). Window drag ran at 0.9 fps because of this (ADR 0014 addendum).
- Do not judge a colour from a screenshot. An active tab that looked light-on-dark measured
  `#3A3A3A` — exactly right. Sample the pixel, or dump the resolved palette at runtime.
- Do not leave `ThemeVariant.Default` and assume the platform is followed; read
  `IPlatformSettings.GetColorValues()` and set Dark or Light explicitly.
- `E:\Development\winmux` and `D:\Development\winmux` are the same project via subst/junction.
