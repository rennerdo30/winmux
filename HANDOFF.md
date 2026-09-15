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
Panes can be renamed — double-click a tab, right-click it, or `Ctrl+B ,` as in tmux — and the name
outranks whatever the program inside calls itself. Terminals scroll back through their history and
support mouse selection with copy. The window wears its own Windows 11 caption: the toolbar lives
*in* the title bar next to the app icon, with minimise, maximise and close drawn by us
([ADR 0016](docs/adr/0016-windows-11-chrome.md)), and the chrome and dialogs are on Fluent 2's type
ramp rather than a size below it.

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`.
**Verified 2026-09-15: 557 passed, 0 warnings.** Version 0.6.0.
Phases 4, 5 and the Phase 6 work are **pushed**; `origin/main` is current as of 2026-09-15.

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
- **Renaming** — `RestoreDescriptor.TitleIsCustom` makes a user-chosen name outrank the automatic
  one, which matters because cmd sets its console title on almost every command and would otherwise
  undo the rename within seconds.
- **Scrollback and selection** — `TerminalViewport` holds the arithmetic (where the view sits,
  what a drag covers) in absolute row coordinates, so a selection survives new output and a parked
  view does not slide. The engine had kept 5,000 rows since Phase 1 with no way to look at them.
- **Windows 11 chrome** — Fluent 2's type ramp (body 14, caption 12, controls 32 tall) replaced a
  ramp one step small throughout, the settings page became rounded cards instead of a label-and-
  combo grid, and the toolbar moved into the title bar. ADR 0016. Most of that session went on a
  bug that was not in the app: see *Do not re-do → Build and tooling*.
- **The command palette became one** — it was created fresh on every dispatch (five presses, five
  stacked windows), kept its system title bar, opened wherever Windows put it, did not close on
  blur, and listed raw action identifiers with no shortcuts. Now a single instance, borderless and
  positioned over its window, with sentence labels and the binding on each row.
  `KeyStroke.Display()` is the new inverse of `Parse`.
- **The pane layer got its first design pass** — panes are inset, rounded tiles on a margined
  canvas; the focused one wears a 2px accent ring drawn in the gutter, so it is visible even while
  a dialog or a foreign pane holds focus. Terminal output is rendered in colour for the first time:
  `TerminalRunSplitter` cuts each row into runs of like style and `TerminalPalette` resolves them
  through Campbell, replacing one hardcoded Nord brush per row that silently discarded every SGR
  attribute the engine had already parsed. The restore modal became a status line, and the status
  bar became three segments carrying the captured cwd with its provenance and the session's save
  state — the two facts that show the product works, previously nowhere in the interface.
- **Scrollback search**, and **symbol bindings on non-US keyboards**. Bindings written as "%" or
  ":" were matched by physical key, which encodes a US layout, so on a German keyboard the palette
  had no key at all; they now match the character Avalonia reports as `KeySymbol`. Search is
  `TerminalSearchModel` plus a find bar built like the palette, bound to the prefix and "/".
- **Tab reordering** by key, menu and pointer drag, and **focus reconciliation**: a new
  `IForegroundWindowMonitor` (Win32 `SetWinEventHook`) tells the shell when the user focuses a
  window one of its panes stands in for, so clicking into an Explorer pane no longer leaves the
  focused pane pointing at a terminal elsewhere (CLAUDE.md section 6).
- **A running window can be dragged into a pane** — `Ctrl+B O` opens a modeless tray of the
  windows already open, and a row dragged onto a pane is adopted there. Dragging the
  application's *own* window onto WinMux is not possible: Windows delivers a window-move to the
  window being moved, not to whatever it passes over, so there is no drop to receive.
- **Snap layouts are back.** `ISnapLayoutService` claims the maximise button's rectangle so
  Windows 11 offers its flyout again — the affordance taken away by drawing our own caption.
  See the 2026-09-15 addendum to ADR 0016 for what claiming a caption button costs.
- **Pane providers load from outside the app.** `providers/` beside the executable, one
  directory each, own `AssemblyLoadContext`, every refusal reported with its reason.
  `samples/WinMux.SampleProvider` is a working `com.example.clock` pane and the reference for
  writing one; `examples/external-provider.toml` opens it. ADR 0012 has claimed since Phase 4
  that a fourth pane kind needs no Core change — that is now demonstrated rather than asserted.

## The next action

**Use it for an hour, then write down what annoyed you.** Everything on the feature list is done and
was seen working on screen; what is left is the class of finding that only comes from use, and this
project has now had two sessions of code-reading produce less than one screenshot did.

The two things still queued are taste calls rather than gaps, both from the 2026-09-15 critique:
the toolbar is uniform icon+label with a divider after every group, where Explorer's command bar has
neither; and `LayoutMetrics` is already parameterised, so a Normal/Compact density setting is a
settings card rather than an architecture change. Neither should be done without deciding it is
wanted.

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
- Do not trust "it lays out correctly but paints nothing" without a DPI-aware screenshot — see
  *Build and tooling* below. The earlier note here, that a right-aligned toolbar group paints
  nothing and the reason is unknown, was very likely that measurement error rather than an Avalonia
  bug. The buttons still sit in the main `StackPanel`; moving them back is untested but should work.
- Do not left-align a child inside a Grid star column and expect it to be clipped to that column.
  `HorizontalAlignment.Left` makes a control take its *desired* width instead of the width it was
  given, and a Grid does not clip children, so it draws straight over the next column. Let it fill
  the column and give the overflow to a ScrollViewer.
- Do not draw a title bar while Avalonia is also drawing one. Avalonia 12 replaced 11's chrome-hints
  enum with `Window.WindowDecorations` (`Full` / `BorderOnly` / `None`) and
  `Avalonia.Controls.Chrome.WindowDrawnDecorations`. `BorderOnly` is the setting that leaves the
  border, shadow and resize grips to Avalonia and the caption to the app; at the default `Full` the
  window title paints twice, overlapping itself.
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
- **Do not screenshot the app from a DPI-unaware process.** PowerShell is one. On this 150% display
  every coordinate it sees is virtualised, so `GetWindowRect` reported 1500x900 for a window that
  was really 2250x1350 and `CopyFromScreen` captured the wrong region — which looked exactly like
  the right-hand end of the chrome failing to render, through several rounds of investigation.
  Call `SetProcessDpiAwarenessContext(-4)` first. The tell that it is the harness and not the app:
  render the control in-process to a `RenderTargetBitmap` and compare.
- Do not resize an Avalonia window with `SetWindowPos` from outside it. Avalonia goes on laying out
  at the size it believes it has, and everything past that width falls outside the real window.
- Do not rasterise an SVG with cairosvg, rlPyCairo or svglib+renderPM — all need a cairo Windows
  does not ship, or fail on rounded rectangles. Headless Chrome works (`assets/build-icon.py`).

**Judgement**
- Do not render a terminal row as one `FormattedText` with one brush. It looks correct on an
  uncoloured prompt and silently throws away every colour and attribute the engine parsed; a run
  per cell is the other wrong answer, at 12,000 text layouts a second on a wide row.
- Do not draw a focused-pane indicator inside the pane, and do not gate it on
  `IsKeyboardFocusWithin`: a native pane paints over anything inside its rectangle, and that flag is
  false exactly when a dialog or the palette has focus, which is when the question is being asked.
- Do not interrupt for success. The restore modal made the first act of every session dismissing a
  paragraph nobody reads, which is how people learn to click OK unread.
- Do not intercept unshifted PageUp in a terminal; it belongs to the program in the pane, which is
  why scrollback uses Shift+PageUp. Anything the user types must also snap the view back to the
  live screen, or typing into history looks like the terminal has frozen.
- Do not let an automatic title overwrite a name the user chose. A shell sets its console title
  constantly, so a rename without `TitleIsCustom` reverts within seconds and looks like a bug in
  the rename rather than in the title handling.
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
