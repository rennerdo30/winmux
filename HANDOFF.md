# HANDOFF — WinMux

*A rolling snapshot, not a changelog. Rewrite it; never append to it. See CLAUDE.md section 10.*

## Where we are

**Phases 0–5 are complete. Phase 6 — the GUI — is most of the way through its first pass.**

WinMux runs terminal panes, foreign-application panes, a file browser and empty panes, all as
providers behind `WinMux.Panes`. The platform layer is extracted, so `WinMux.Shell` declares zero
`DllImport` ([ADR 0013](docs/adr/0013-phase-5-platform-layer.md)). Tab groups nest anywhere with a
strip on any edge ([ADR 0014](docs/adr/0014-nested-tab-groups-and-shell-chrome.md)). Any installed
application can be put in a pane through a Start Menu picker, kept as a profile, or adopted from a
window that is already open ([ADR 0015](docs/adr/0015-profiles-app-catalog-and-empty-panes.md)).

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`.
**Verified 2026-09-15 at `c421c9b`: 398 passed, 0 warnings.** Version 0.6.0.
**Nothing has been pushed since `68103d5`** (Phase 3), which is where `origin/main` still sits.
Everything after it — Phases 4, 5 and all of the Phase 6 work — exists only on this machine.
(A count of commits is deliberately not written here: it would be wrong the moment this file is
committed, which is the smallest possible example of why section 10 exists.)

Run it: `run.cmd`, or `scripts/run.ps1 -Session examples/tabs-and-splits.toml`.
Package it: `publish.cmd` → `dist/WinMux-0.6.0-win-x64/` and a zip.

## What just happened

**2026-09-15**, one long session (see `git log` from `68103d5` for the commits):

- **Phase 5** — `WinMux.Platform` extracted; `Win32Interop.cs` deleted rather than ported, because
  18 of its 24 imports had no callers. ADR 0013.
- **Phase 6, chrome** — per-stack tab strips as reserved layout geometry, a toolbar, Mica, system
  accent and light/dark, vector icons, visible divider handles. ADR 0014 and its addenda.
- **Phase 6, profiles** — `LaunchProfile`, `IAppCatalog` (147 apps in 199 ms from the Start Menu),
  empty panes, window adoption via `PaneHost --adopt`, settings, open/save-as, an app icon.
  ADR 0015.
- **Performance** — a window drag ran at **0.9 fps**; now **1,014/sec**. The cause was
  `SnapshotProcesses` (123 ms, ~330 processes) on the interactive path, once per terminal pane,
  on every title change. See the addenda to ADR 0014.
- **Build** — the shell had been shipping a stale `WinMux.PaneHost.exe` because the copy target
  guessed the wrong output directory; on a clean clone it would have copied nothing, silently.

## The next action

**Use it for an hour and see what breaks.** Enough changed today that the next real finding will
come from use, not from code reading. Two things in particular have never been seen on screen by
the author: a foreign app inside a tab group (does Explorer still paint its own content, and does a
tab strip beside it survive?), and the empty-pane launcher adopting a running window.

Then, in rough order of what the product is missing:
**terminal selection and scrollback** (the engine keeps scrollback and the control has no wheel
handler — the sharpest gap), tab and pane **reordering**, **foreign-window focus reconciliation**,
dragging a window into a pane, provider discovery and packaging.

## Blocked / needs a human

- **Mixed-scale multi-monitor is unmeasured.** The development machine has two monitors at the
  same scale, so nothing has exercised mixed DPI. Someone must set different display scales and run
  `scripts/phase4-demo.ps1` across them before fidelity is claimed.
- **Input-queue starvation is unmeasured** (ADR 0001, finding 4). Until it is, keep PaneHost
  top-level and never reparent it into the shell.
- **Higher-integrity attach is constrained by UIPI.** Failures are visible; WinMux will not elevate.
- **`Terminal.Emulation` 0.3.3 has no public source.** A supply-chain decision before v1. The owned
  `ITerminalEngine` seam bounds the replacement cost but does not make the call.
- **An X11 port needs its own host executable, not a shim.** PaneHost keeps its 35 imports
  deliberately (ADR 0013, decision 5). Do not read that as unfinished Phase 5 work.
- **Nothing is pushed.** Everything after `68103d5` exists only in this checkout, so a lost disk
  is a lost project. Pushing is the user's call; it has not been made.

## Do not re-do

**Architecture and process model**
- Do not hand-roll ConPTY (ADR 0002) or `SetParent` into an Avalonia window — the latter succeeds,
  reports the right parent and rect, and paints nothing (ADR 0007).
- Do not call a foreign HWND from the UI thread, and never hard-kill a host that still owns a
  child (ADR 0008, ADR 0011).
- Do not add a method to `WinMux.Platform` before something calls it (ADR 0013).

**Performance**
- Do not put `SnapshotProcesses` on an interactive path: 123 ms for ~330 processes against 0.12 ms
  for the working-directory read it enables. `CachedProcessInspector` shares one list.
- Do not call `SessionController.RequestSave()` from anything firing at input frequency. Debouncing
  the *write* does not help when producing the snapshot is the expensive part.

**Avalonia and chrome**
- Do not write `Rect`, `TabStrip` or `Path` unqualified in a file importing Avalonia; assume any
  short geometric or widget-shaped name in Core has an Avalonia namesake.
- Do not set `Background` directly on a Button — it replaces what Fluent's template animates and
  silently kills hover and pressed feedback. Style the templated `ContentPresenter`.
- Do not put a right-aligned group in the toolbar: docked right, or in a Grid `Auto` column, it
  reports sensible bounds and paints nothing. Unexplained (ADR 0014). Use the main `StackPanel`.
- Do not leave `ThemeVariant.Default` and assume the platform is followed; read
  `IPlatformSettings.GetColorValues()` and set Dark or Light explicitly.
- Do not reuse the horizontal tab-strip thickness for a vertical strip (28px of titles is a column
  of ellipses); `LayoutMetrics` carries both.
- Do not put palette or modal chrome over the pane canvas; native windows paint above it.

**Build and tooling**
- Do not `dotnet build` a single x64 project then run `bin/x64/Release/...` — a bare project build
  lands in `bin/Release/` and you test a stale exe. Build the solution.
- Do not guess another project's output path in a copy target; ask MSBuild with `GetTargetPath`.
- Do not run `scripts/phase*-demo.ps1` under Windows PowerShell 5.1; the verifiers need pwsh 7.
- Do not rasterise an SVG with cairosvg, rlPyCairo or svglib+renderPM — all need a cairo Windows
  does not ship, or fail on rounded rectangles. Headless Chrome works (`assets/build-icon.py`).

**Judgement**
- Do not ship a capability with no interface. Foreign-app panes existed from Phase 3 with no way to
  create one; pane resizing had no handle. Both went unnoticed for months (CLAUDE.md section 5a).
- Do not judge a colour from a screenshot: an active tab that looked light-on-dark measured
  `#3A3A3A`, exactly right. Sample the pixel or dump the resolved values.
- Do not assert exact requested rectangles for arbitrary apps; DPI rounding and minimum sizes are
  measured counterexamples (ADR 0003).
- Do not rank every OSC report over a newer deepest-child PEB, or rely on Porta.Pty's Windows
  quoting (ADR 0010).
- Do not pass a lifetime token where an operation token belongs, and do not let action dispatch
  return before async pane creation completes (ADR 0012).

**Environment**
- The development checkout is reachable by two drive letters via subst/junction, so a tool may
  report a path that looks unfamiliar. It is the same working tree.
- Opening a session file under `examples/` rewrites it — `scripts/run.ps1` copies it aside first.
