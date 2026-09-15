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

**2026-09-15**, one long session. `git log 68103d5..` is the changelog; this is what a newcomer
needs to know happened, and where the reasoning lives.

- **Phases 4 and 5 closed.** Pane providers and the file browser (ADR 0012); the platform layer
  extracted so the shell declares zero `DllImport` (ADR 0013).
- **Phase 6, the GUI.** Nested tab groups and shell chrome (ADR 0014), profiles and the app
  catalogue (ADR 0015), then a Windows 11 pass over the chrome, the dialogs and the pane layer
  (ADR 0016): Fluent 2 metrics, a caption the app draws itself with snap layouts claimed back,
  inset rounded panes, an accent focus ring in the gutter, and terminal output rendered in colour
  for the first time.
- **Everything on the feature list landed**: scrollback search, tab reordering by key, menu and
  drag, focus reconciliation against the OS, dragging a running window into a pane, and pane
  providers loaded from `providers/` beside the executable with a working sample.
- **Two long-standing claims were finally measured, and one was wrong.** Input-queue attachment
  does *not* starve the shell of input — it blocks *focus* for the duration of a wedge, 201 ms
  against 4,386 ms (ADR 0017). Symbol key bindings never worked on this machine at all, because
  `KeyStroke`'s table assumes a US ANSI keyboard and the hardware here is Japanese 106/109.
- **Three separate times, a measurement harness produced the alarming result rather than the
  product.** That is the most transferable thing this session produced; see *Do not re-do* and
  CLAUDE.md section 7.
- **Dependencies are hash-locked** and `Terminal.Emulation` reassessed (ADR 0018).

## The next action

**Use it for an hour, then write down what annoyed you.** Everything on the feature list is done and
was seen working on screen; what is left is the class of finding that only comes from use, and this
project has now had two sessions of code-reading produce less than one screenshot did.

The two things still queued are taste calls rather than gaps, both from the 2026-09-15 critique:
the toolbar is uniform icon+label with a divider after every group, where Explorer's command bar has
neither; and `LayoutMetrics` is already parameterised, so a Normal/Compact density setting is a
settings card rather than an architecture change. Neither should be done without deciding it is
wanted.

## Waiting on you

Three things, and only three. Each needs a person, a machine setting or a judgement — none is
unfinished engineering, and nothing in the codebase is waiting on them.

- **Run the mixed-DPI check.** Set one display to a different scale, then
  `scripts/verify-mixed-dpi.ps1`. It refuses to report anything while the scales match, launches
  WinMux across the seam, and lists what to look at. This is the last measurement in the project
  that has never been taken, and it needs two monitors at different scales — which this machine has
  never had.
- **Decide `Terminal.Emulation`.** [ADR 0018](docs/adr/0018-terminal-emulation-supply-chain.md)
  has the facts, four costed options and a recommendation: ask the author to publish the source,
  keep the hashes pinned, and benchmark `tomlm/Iciclecreek.Avalonia.Terminal` before assuming a
  replacement is expensive. Nobody has asked the author; that is a message, not a commit.
- **Use it for an hour.** See *The next action*.

## Standing constraints

**These are not open work.** They are decided, implemented and permanent, and they live here so
that a future session recognises them as answers rather than rediscovering them as questions.

- **Higher-integrity attach is constrained by UIPI**, and always will be. A non-elevated process
  cannot manipulate an elevated application's windows. Failures are detected and explained in plain
  words, and **WinMux will not ship elevated to work around it** (CLAUDE.md section 5).
- **An X11 port needs its own host executable, not a shim.** `WinMux.PaneHost` keeps its 35 imports
  deliberately; reparenting *is* a platform implementation (ADR 0013, decision 5). This is the
  design, not a gap in it.

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

**Keyboards**
- Do not assume `KeyStroke`'s symbol table describes the keyboard in front of you. It is US ANSI,
  and this machine is Japanese 106/109: `:` unshifted, `"` on Shift+2. `VkKeyScanEx` answers the
  question for every installed layout in a few lines of PowerShell — do that before theorising
  about why a symbol binding does not fire.

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
