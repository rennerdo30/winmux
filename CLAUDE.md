# CLAUDE.md — WinMux

Working notes for implementing this project. Read `README.md` first for the product idea,
then **`HANDOFF.md`** for where the work actually stands right now.
This file is the architecture contract: the decisions that are made, the ones that are open,
and the traps that will eat days if ignored.

**`HANDOFF.md` is not optional and not a courtesy.** Read it before doing anything, and rewrite it
before you stop — and again whenever a change makes it wrong. Section 10 says how, and why the last
session got it wrong.

**State: Phase 5 complete; Phase 6 (the GUI) well under way.** `WinMux.exe` runs persistent ConPTY/VT terminal panes, foreign
applications selected through a shipped quirks database and isolated in one top-level PaneHost
per pane, and a built-in file browser — all three as **pane providers** behind the public
`WinMux.Panes` contract, so a fourth kind needs no Core or layout change
([ADR 0012](docs/adr/0012-phase-4-pane-providers-and-file-browser.md)). Both embed and attach
modes, automatic refusal fallback, and live switching ship. The platform layer is extracted:
`WinMux.Platform` states what an OS must provide and `WinMux.Platform.Win32` provides it, so the
shell declares **zero P/Invoke** ([ADR 0013](docs/adr/0013-phase-5-platform-layer.md)). Tab groups
nest anywhere in the tree, each with its own strip on any edge, and every layout operation is a
toolbar button as well as a key ([ADR 0014](docs/adr/0014-nested-tab-groups-and-shell-chrome.md)).
**Profiles** put any installed application in a pane, found through the Start Menu; an empty pane
offers them, along with the windows already open, which it can adopt
([ADR 0015](docs/adr/0015-profiles-app-catalog-and-empty-panes.md)).
Phase 0:
ADRs [0001](docs/adr/0001-out-of-process-pane-hosts.md),
[0002](docs/adr/0002-terminal-stack.md), [0003](docs/adr/0003-foreign-app-compatibility.md),
[0004](docs/adr/0004-cwd-capture.md). Product code: `WinMux.Core` (layout, session model, TOML —
ADRs [0005](docs/adr/0005-layout-engine.md), [0006](docs/adr/0006-session-file-format.md)),
`WinMux.Pty`, `WinMux.Terminal`, `WinMux.Shell`, `WinMux.PaneHost`, and `WinMux.Cli`; see
[ADR 0008](docs/adr/0008-pane-host-ipc.md) and
[ADR 0009](docs/adr/0009-phase-1-terminal-runtime.md), with Phase 2 closed by
[ADR 0010](docs/adr/0010-phase-2-persistence-runtime.md), and Phase 3 by
[ADR 0011](docs/adr/0011-phase-3-foreign-app-runtime.md).

**Not done yet:** physical mixed-scale multi-monitor verification, provider discovery/packaging,
tab/pane reordering, dragging a running window into a pane, foreign-window focus reconciliation,
terminal selection and scrollback navigation.

---

## 1. Priorities, in order

When two goals conflict, the higher one wins. This ordering is the product.

1. **Session persistence.** Restoring the layout tree, tabs, per-pane program and working
   directory is the reason this exists. If a feature endangers reliable save/restore, it loses.
2. **Stability of the shell.** A misbehaving pane must never take down or freeze WinMux.
3. **Terminal panes that feel native.** Correct VT, fast, no input lag, sane copy/paste.
4. **Foreign-app panes.** High value, high risk. Must degrade gracefully, never crash the host.
5. **File browser pane.**
6. **Cross-platform.** A design discipline (keep the core portable), not a shipping commitment.

## 2. Stack

**Decided: .NET 10 + Avalonia UI, C#.** Confirmed by spike 1 —
[ADR 0002](docs/adr/0002-terminal-stack.md). (This said ".NET 9" before Phase 0; 10 is what is
installed and what the terminal packages target.)

Why:
- Win32 interop is the bulk of the hard work here, and C# + [CsWin32](https://github.com/microsoft/CsWin32)
  gives typed P/Invoke with no hand-written signatures.
- Avalonia's `NativeControlHost` is purpose-built for embedding native handles, and it has both a
  Win32 and an X11 implementation — the cross-platform door stays open for free.
- ConPTY, process inspection and window manipulation all have direct, well-documented .NET paths.

~~Main risk: **terminal rendering.**~~ **Retired by spike 1.** The plan was that .NET has no
terminal control of Windows-Terminal quality and that writing one might sink the stack. Measured:
`Terminal.Emulation` is a VT500 engine that parses at 14–36 MiB/s — 3–10× faster than ConPTY
delivers — with correct reflow, alternate screen, double-width cells and OSC 8. **Adopt it, behind
our own `ITerminalEngine` interface**, because it is a 5-week-old single-author package whose
source repository is not public. Its types must never reach `WinMux.Core`.

Two numbers worth carrying forward:
- **ConPTY's round-trip floor is ~0.08 ms** (cmd). A full 60 Hz frame is available for rendering.
- **PowerShell's own echo latency is ~15.6 ms** through the identical pty. That is PSReadLine, not
  us, and it cannot be fixed from here. Benchmark our input path against cmd or it will be masked.

For the pty itself: **adopt, do not hand-roll.** Hand-rolled ConPTY has a silent failure mode
(section 5 of ADR 0002) that will cost days. `Porta.Pty` is the recommendation for `WinMux.Pty` —
independently maintained, widely used, and already carrying Linux/macOS backends.

Rejected, with reasons worth remembering:
- **Tauri / WebView2 + xterm.js** — excellent terminals and chrome, but embedded apps become native
  child HWNDs composited *over* a webview. Z-order and input routing fights, permanently.
- **WinUI 3 / WPF** — best-in-class interop, but forecloses portability entirely.
- **Rust + custom GPU renderer** — the most control and by far the most work. Reconsider only if
  terminal rendering forces a rewrite anyway.

## 3. Repository layout

Names are indicative; the *separation* is the requirement.

```
WinMux.Core/             layout tree, session model, config, keymap, persistence — NO platform APIs   [EXISTS]
WinMux.Pty/              ConPTY / pty abstraction, terminal process lifecycle                  [EXISTS]
WinMux.Terminal/         owned VT-engine contract and adapter                                   [EXISTS]
WinMux.Panes/            public IPaneProvider / IPaneRuntime contract — Core + Avalonia only     [EXISTS]
WinMux.Platform/         IHostWindowService + friends: the platform contract, net10.0, no P/Invoke  [EXISTS]
WinMux.Platform.Win32/   Windows/: the implementations. ForeignApps/: quirks database            [EXISTS]
WinMux.PaneHost/         the out-of-process pane host executable (see section 5)                 [EXISTS]
WinMux.Shell/            Avalonia app: chrome, rendering, input, overlays                          [EXISTS]
WinMux.Cli/              `winmux` — the command line surface (section 6)                             [EXISTS]
WinMux.Tests/                                                                                        [EXISTS]
assets/                  winmux.svg (the source of the mark) and the .ico built from it        [EXISTS]
docs/adr/                one short file per architectural decision
```

All projects now exist. See
[ADR 0005](docs/adr/0005-layout-engine.md) for the layout engine's decisions and invariants.
The `Columns`/`Rows` vocabulary in `SplitDirection` is deliberate — never `Horizontal`/`Vertical`,
which every multiplexer defines differently.

**`WinMux.Core` must not reference any platform assembly.** Enforce it with a test that asserts
the dependency set. Everything portable lives there; if the layout engine ever needs an `HWND`,
the design has gone wrong.

**`WinMux.Platform` is the shape of an operating system, never an implementation of one**
([ADR 0013](docs/adr/0013-phase-5-platform-layer.md)). It targets `net10.0` with no OS suffix,
references Core alone, and contains no P/Invoke. A platform call reaches it through
`IHostWindowService`, `IProcessInspector` or `IUserNotifier`, stated as intent — the destination,
never the steps, because the steps are what differ between systems. `WinMux.Shell` declares zero
`DllImport`, and `WinMux.Shell/PlatformServices.cs` is the one file in it that names a concrete OS.
`WinMux.PaneHost` is the deliberate exception: reparenting *is* a platform implementation, and it
needs its own process for the reason in ADR 0001, so an X11 port gets its own host binary rather
than a shim. `PlatformBoundaryTests` enforces all of this, and every one of its guards has been
mutation-tested — the obvious `GetReferencedAssemblies()` check alone cannot fail.

## 4. Model

```
Session
 └── Window (top-level; one OS window)
      └── LayoutNode (tree)
           ├── Split   { orientation, children[], ratios[] }
           ├── Stack   { children[], activeIndex }   // tabs
           └── Leaf    { Pane }

Pane = { id, kind, title, PaneState }
Stack additionally carries `TabStrip`: Top | Bottom | Left | Right
  kind: validated, case-normalized string — "terminal" | "file-browser" | "foreign-app" | "com.example.…"
```

**`PaneKind` is a string, not an enum** ([ADR 0012](docs/adr/0012-phase-4-pane-providers-and-file-browser.md)).
A closed enum made providers extensible in name only: a provider in another assembly could not
define a kind. Built-in identifiers are stable; unknown ones round-trip through TOML untouched, so
a descriptor is never destroyed merely because its provider is absent. A pane and its restore
descriptor must carry the same kind.

Every pane kind is a **provider** implementing `IPaneProvider`/`IPaneRuntime` from the public
`WinMux.Panes` assembly: create, attach to a rect, resize, focus, capture state, prepare to close,
dispose. `MainWindow` owns only layout, action routing, chrome and generic runtime coordination.
Adding a pane kind must not touch Core, the tree, persistence or the CLI.

The file browser is the documented exception to "a pane is a process": it is trusted in-process
WinMux UI, because a subprocess boundary buys nothing there. Terminal and foreign-app panes keep
theirs.

A `Stack` may appear anywhere a node may, including inside another `Stack`
([ADR 0014](docs/adr/0014-nested-tab-groups-and-shell-chrome.md)). Its tab strip is **reserved
geometry**, not decoration: `Layouter` carves the band out of the stack's rectangle before placing
the active child, and reports it on the `Arrangement`. That is what makes the strip safe to draw
(section 6) and what makes nested tab groups expressible at all — a single bar docked to the window
can only ever describe one stack.

**Restore descriptor** — the persisted per-pane payload:
- `kind`, `title`
- `program` (resolved absolute path), `args`, `env` overrides
- `cwd` — the single most important field
- kind-specific extras (file browser: current directory + selection; foreign app: match rules)

Restore recreates processes from descriptors. It does **not** restore process state. Say so
plainly in the UI so nobody expects otherwise.

### Capturing the working directory

The known-hard part; Windows Terminal itself has an
[open issue on this](https://github.com/microsoft/terminal/issues/14270). Layered strategy,
best available wins:

1. **`OSC 9;9` / `OSC 7`** — the shell reports its cwd. Ship opt-in profile snippets for
   PowerShell, pwsh, cmd and bash that emit it. This is the only accurate method for shells
   with child processes running.
2. **Query the process** — walk to the deepest child of the pane's process and read its cwd
   via the PEB. Works without shell cooperation, needs matching bitness and access rights.
3. **Fall back to the launch cwd.** Never fail the whole save because one pane is unknown.

**Measured in spike 4** ([ADR 0004](docs/adr/0004-cwd-capture.md)) — the layered strategy
succeeds **85% (34/40)**, and the layers really are complementary. Per-shell reality:

| shell | OSC (with snippet) | PEB (root) | PEB (deepest) |
|---|---|---|---|
| pwsh / powershell | 80% | **20%** | 60% |
| cmd | 100% | 80% | 100% |
| bash under WSL | 80% (stock too, on Debian) | **0%** | **0%** |

- **PowerShell's PEB is permanently stale**: `Set-Location` updates the provider location, not
  the process working directory. Strategy 2 does not work for the default shell — 20% is only
  the never-moved case.
- **For WSL the PEB is meaningless, not merely unreliable** — it returns Windows paths
  (`C:\WINDOWS`) for a Linux shell. Mark WSL panes OSC-only; a confidently wrong answer is worse
  than none.
- **The snippets are therefore mandatory, not a power-user extra.** They live in
  [`spikes/04-cwd/profiles/`](spikes/04-cwd/profiles) and ship. Offer to install them, and say
  plainly what is lost otherwise — a bare PowerShell pane restores to the wrong directory the
  moment the user changes directory, which is the headline feature failing silently.
- **OSC survives ConPTY intact**, including paths with spaces and non-ASCII.
- **OSC goes stale inside a nested non-cooperating shell**; PEB-of-deepest-child covers exactly
  that case. Useful accident: cmd's `PROMPT` is an environment variable so nested `cmd` keeps
  reporting, whereas a PowerShell prompt *function* is not inherited.
- **Record which strategy produced each value, and when.** A stale OSC report and a live PEB read
  do not deserve equal trust on restore.
- Unsolved: stock PowerShell, after a `cd`, with no child process. Nothing recovers it.

Persist *timestamped* cwd snapshots continuously, not only at exit — a crash must not cost the
session. Save on a debounce after any layout change, and on cwd change.

### Session file

**Decided: TOML** — [ADR 0006](docs/adr/0006-session-file-format.md). Versioned from the very first
write, with a migration path. Sessions are user data: a failed load is a bug worth a backup file,
never a reason to silently start empty.

The layout tree is **flattened** into `[[windows.nodes]]` with generated ids, because nesting TOML
tables to match the tree produces `[[windows.root.children.children.children]]` — worse than the
JSON it was meant to improve on. Panes are flat `[[windows.panes]]` tables and are explicitly the
part worth hand-editing. Paths are written as TOML literal strings so backslashes survive unescaped.

Implemented in `WinMux.Core/Session`: `SessionFile.Save`/`Load` is the whole surface. Saving is
atomic; loading an unreadable file quarantines a copy and refuses rather than starting empty.
Reading rejects dangling references, duplicate ids, unreachable nodes and cycles by name.

## 5. Embedding foreign apps — read before writing any code

[Raymond Chen: cross-process parent/child windows](https://devblogs.microsoft.com/oldnewthing/20130412-00/?p=4683)
is required reading. The traps, each of which has bitten shipping products:

- **Synchronous cross-process window calls.** *The trap that actually fires* — measured in spike 3,
  [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md). `SetWindowPos` on a wedged app's window
  blocks the caller for as long as the app is wedged, because it sends `WM_WINDOWPOSCHANGING`
  synchronously to a thread that is not pumping. The shell froze for the full 6 s. **This hits
  attach mode exactly as hard as embed mode** — no `SetParent` is involved. The rule that follows:
  *the UI thread never makes a synchronous window call against a foreign window.* Pane geometry
  goes through a dedicated layout thread or `SWP_ASYNCWINDOWPOS`; every other cross-process call
  (`SendMessage`, `SetFocus`, `DestroyWindow`, `SetParent`) has the same hazard and no async flag.
- **Input queue attachment.** `SetParent` across processes attaches the two threads' input queues,
  *transitively*. One hung app hangs everyone attached to it — including the WinMux UI thread.
  **This is why pane hosting is out-of-process** (below). Non-negotiable.
  *Status: unverified.* Spike 3 chained `shell → host → app` and the shell stayed responsive, but
  the harness measures message-loop liveness and cannot see input starvation, which is this trap's
  actual symptom. Treat as true and unproven: do not chain `SetParent` from the shell to a pane
  host until someone tests it with synthesized input.
- **DPI mismatch.** Hosting an app with different DPI awareness misbehaves unless mixed-mode
  hosting is enabled explicitly (`SetThreadDpiHostingBehavior(DPI_HOSTING_BEHAVIOR_MIXED)`).
  Declare WinMux per-monitor-v2 and test on a mixed-DPI multi-monitor setup — it is not optional,
  it is where the bugs live.
- **Window styles.** `SetParent` does not fix styles. Add `WS_CHILD`, clear `WS_POPUP` and the
  caption/border bits, then `SetWindowPos(..., SWP_FRAMECHANGED)`. Record every original style,
  the original parent and the original rect — restoring them exactly is what makes detach safe.
- **UIPI.** A non-elevated process cannot manipulate an elevated app's windows. Detect and say so
  in plain words. Do not ship an elevated WinMux to work around it.
  Measured in spike 2: Task Manager refuses with `SetParent` → **`ERROR_ACCESS_DENIED` (5)**.
  Note `OpenProcess` for query *succeeds* on it, so probing the process is **not** a valid UIPI
  test — only the error from `SetParent` itself is. UWP refuses differently:
  **`ERROR_INVALID_PARAMETER` (87)**. Map both to plain words and fall back to attach mode.
- **Verifying a reparent.** Two traps, both measured in spike 2:
  `SetParent` returning the old parent is not success — a failure returns `NULL` and sets the
  error, so **capture `GetLastError` on the very next line**; any other Win32 call clobbers it.
  And **`GetParent` returns the OWNER for a `WS_POPUP` window**, not the parent — verify with
  `GetAncestor(hwnd, GA_PARENT)`, which yields the desktop for a genuine top-level window.
- **Which window?** Apps show splash screens, tool windows and hidden helpers. Adopt only visible,
  top-level, non-owned windows with a real title, and poll with a timeout rather than assuming the
  first window is the right one. Spike 2 confirms this rule is load-bearing: Notepad alone creates
  **12** top-level windows (`GDI+ Hook Window Class`, three `IME`, two `MSCTFIME UI`,
  `tooltips_class32`, `CtrlNotifySink`…) and the rule picked correctly on every target.
  **Match on process image name and window class, never on the launched pid** — on Windows 11
  `notepad.exe` is a *shim* that runs the packaged `Notepad.exe` as a different process.
- **A pane's actual rect will not equal its requested rect.** A DPI-unaware window hosted at 150%
  came back consistently **one pixel wider** than asked (700→701, 880→881) from DPI virtualization
  rounding, and apps with minimum sizes miss by more. Layout code must never assert equality.
- **Detach must always work.** Every embed is undoable: on clean exit, on crash, and on a panic
  hotkey. An orphaned invisible child window is a lost application, and users will not forgive it.
  Measured in spike 3: killing a pane host that still owns an embedded window **destroys that
  window**, leaving the app process alive with nothing on screen — unrecoverable. Always detach
  *then* terminate; never the other way round. Graceful detach restores parent, style, ex-style
  and rect byte-exactly, so the mechanism is sound — the ordering is what kills.

### Hosting a foreign window inside the shell — measured in Phase 1

ADR 0007's direct `NativeControlHost` path rendered, but put the foreign app in the shell's own
window/lifecycle boundary. It is superseded by [ADR 0008](docs/adr/0008-pane-host-ipc.md): each app
is now a child of a disposable `WinMux.PaneHost`, while that host remains an owned top-level window
positioned over its pane. The shell never calls the foreign app's HWND.

Strip and restore the app's frame in PaneHost, and never hard-kill either shell or host while the
app is parented. Graceful `DETACH` restores parent, styles, ex-styles, and rectangle; `CLOSE`
restores first and then asks the app to close. `explorer.exe` window reuse still requires matching
by class and excluding windows already claimed by another pane.

### Out-of-process pane hosts

Each foreign-app pane gets its own **`WinMux.PaneHost`** process owning a borderless host window;
the app is reparented into *that*. The shell positions pane-host windows to match pane rectangles
and currently talks to them over line-oriented redirected standard streams. A named-pipe upgrade
is optional when the protocol needs richer lifecycle or focus commands.

The cost is real — IPC, lifecycle management, focus and z-order coordination. It buys the one thing
priority 2 demands: a wedged app freezes its own host process, and the shell stays alive, redraws,
and can kill or detach the pane. Do not "simplify" this away in Phase 3; it is the whole reason
the design survives contact with real applications.

**Confirmed by spike 3 — for a narrower reason than the above assumes.** The shell survived a
wedged app in both out-of-process topologies, because it only ever calls window APIs on the
*host's* window, and the host keeps pumping while the app inside it is stuck. The isolation comes
from never touching the foreign window, not from anything about input queues. Two consequences:
the conclusion stands, and the pane host must stay **top-level and positioned** — the shell must
not `SetParent` it into the shell window. See [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md).

### Attach mode

The fallback for apps that resist embedding: leave the window top-level, drive its position/size
to follow the pane rect, like a tiling WM. Less seamless, dramatically more compatible. Every app
must be switchable between embed and attach **at runtime**, and the choice is remembered per app
in the quirks database.

Implemented in Phase 3: PaneHost owns both transitions and emits the effective strategy. Prefix
`A` / `toggle-foreign-host-strategy` switches the focused pane without launching a second app;
the shell persists the resulting explicit override. An embed refusal restores first, then attaches
the same HWND and surfaces the reason.

### Quirks database

A shipped, user-extendable data file: match on executable/class/title, mapping to strategy,
window-selection rule, launch delay and known limitations. Assume every non-trivial app needs an
entry eventually. Verified-app coverage is a documented feature, not an implementation detail.

**Schema and first entries seeded by spike 2**: [`spikes/02-reparent/quirks-seed.json`](spikes/02-reparent/quirks-seed.json).
Each entry carries match (exe + class), strategy, window-selection rule, launch delay, limitations
and a `verified` block (date, OS build, result). Defaults established: **UWP/packaged apps and
higher-integrity apps → attach**; ordinary Win32, Chromium and the shell → embed.

Implemented in Phase 3 by `WinMux.Platform.Win32`. The measured seed is copied beside
`WinMux.exe` as `foreign-app-quirks.json`, where it is deliberately user-editable. Matching accepts
executable image, class, and optional title substring; malformed/future files stop foreign-app
launch visibly rather than being ignored. The shell resolves rules but never calls a foreign HWND.

## 5a. Profiles — the extension point users touch

WinMux is a **power-user Windows application**, and the bar for its interface is the same one it
sets for its internals: if WinMux can do something, there is a way to ask for it without editing a
file. A capability with no interface is not a feature, it is a note to the author.

That rule failed twice and both failures are instructive. Pane resizing worked from
[ADR 0005](docs/adr/0005-layout-engine.md) and had no handle, no cursor and no hover state, so
nobody knew it existed. Foreign-app panes shipped in Phase 3 and could only be created by
hand-writing a TOML file, so in practice nobody could put an application in a pane at all.

**A profile is a named thing you can put in a pane.** One concept covers terminals and
applications, because to the layout they are the same: a program, its arguments, a working
directory, and — for a windowed application — how to find and host its window.

- Profiles are **user data**, stored beside the settings, not compiled in. The shipped terminals
  are seeded on first run and are editable and deletable like any other.
- A profile is created from **the installed-application catalog** (the Start Menu, resolved through
  `IAppCatalog`), from **browsing for a program**, or by typing one in. The catalog is the default
  path because a power user should not have to know where an executable lives.
- Anything that opens a pane — the toolbar, the palette, the CLI, an empty pane's launcher —
  offers the same profile list. Adding a profile adds it everywhere at once.
- `PaneKind` stays the provider contract
  ([ADR 0012](docs/adr/0012-phase-4-pane-providers-and-file-browser.md)). A profile chooses **which
  provider** runs and **what it runs**; it is not a new pane kind.

**An empty pane is a first-class state.** A pane with nothing in it shows the same launcher —
profiles, the app catalog, and the running windows it could adopt — so "make a pane, then decide"
is a supported workflow rather than a gap between splitting and choosing.

**Resolving a shortcut is platform work.** Enumerating the Start Menu and following a `.lnk` to its
target is Win32, so it lives behind `IAppCatalog` in `WinMux.Platform` and is implemented in
`WinMux.Platform.Win32` — the shell keeps its zero `DllImport`
([ADR 0013](docs/adr/0013-phase-5-platform-layer.md)).

## 6. UI constraints

- **Native child windows always paint above the host's own drawing.** Any pane hosting a native
  window will occlude UI drawn beneath it. Design around it: keep chrome (tab bar, status line)
  in regions that never overlap panes.
- **Transient overlays** — command palette, pane picker, split preview — must be **separate
  top-level layered windows**, not in-canvas elements. They will be occluded otherwise.
- **Focus is explicit.** With processes owning their own windows, focus follows `WM_ACTIVATE` and
  friends, not the UI framework's notion. Maintain WinMux's own focused-pane state and reconcile
  it with the OS; never assume they agree.
- **Keymap: one binding table, tmux-style prefix by default** (configurable, no-prefix allowed).
  Every action addressable by name from the command palette and from a CLI (`winmux split -h`),
  because a CLI makes the whole thing scriptable and testable.
- **The prefix is the fast path, never the only path.** Every layout operation is also a toolbar
  button and, for tabs, a control on the strip itself. All four surfaces — key, button, palette,
  CLI — dispatch the *same named action*, so there is one implementation and four ways to ask.
  An action may ship without a key (`move-tabs-*` do), but never without a visible control; the
  keymap tests enforce the exemption list rather than letting it drift.
- **Chrome colours live in `WinMux.Shell/Chrome/Theme.cs`**, and interaction states are Avalonia
  *styles*, not properties set per control. Setting `Background` on a Button replaces the value
  Fluent's template animates and silently removes its hover and pressed feedback.
- **Sizes live there too, and they are not a matter of taste.** `Palette` carries Fluent 2's ramp —
  body 14px, caption 12, subtitle 20, controls 32 tall, gaps of 8/12/20 — because Windows 11 is
  specified at those numbers and a chrome built one step smaller reads as an older application
  wearing a dark theme. New chrome takes the ramp; it does not invent a size
  ([ADR 0016](docs/adr/0016-windows-11-chrome.md)).
- **The window owns its caption.** `WindowDecorations.BorderOnly` leaves Avalonia drawing the
  border, shadow and resize grips and hands the title bar to `Chrome/TitleBar.cs`, which puts the
  toolbar in it beside the app icon. Snap layouts come back through `ISnapLayoutService`, which
  claims the maximise button rectangle — and with it that button’s clicks and hover, which the
  system then drives. See the 2026-09-15 addendum to ADR 0016 before touching either.
- **A screenshot is evidence only from a DPI-aware process.** Chrome that lays out correctly and
  appears to paint nothing is, on this machine, more likely a virtualised capture than an Avalonia
  bug — a whole session was spent on that once. Render to a `RenderTargetBitmap` in-process to tell
  the two apart, and sample pixels rather than judging colours by eye.

## 7. Phase 0 — spikes (do these first)

Throwaway code, in a `spikes/` folder, deleted once the ADRs are written. Nothing else starts
until all four have an answer. **All four now have answers — the gate is open.** The measured
quirks data now ships from `WinMux.Platform.Win32/ForeignApps/foreign-app-quirks.json`; the spike
copy remains evidence. The cwd profiles moved into `WinMux.Shell/Cwd/Profiles/` for Phase 2.

1. ~~**ConPTY pane.**~~ **Done, 2026-09-10.** Spawn pwsh, render VT, resize correctly, no input lag.
   Stack confirmed, VT engine adopted rather than written. Rendering itself is *not* covered — the
   spike validates the cell grid, not glyph rasterisation or paint latency. See
   [ADR 0002](docs/adr/0002-terminal-stack.md) and `spikes/01-conpty/`.
2. ~~**Reparent four apps**~~ **Done, 2026-09-10.** Notepad, Character Map, Explorer, VS Code and a
   packaged/UWP app, plus synthetic DPI-awareness levels. Ordinary apps including Chromium and the
   shell embed and detach byte-exactly; Task Manager and UWP refuse with distinct, detectable
   errors. **True mixed-DPI multi-monitor remains untested** — both monitors here are 144 DPI. See
   [ADR 0003](docs/adr/0003-foreign-app-compatibility.md) and `spikes/02-reparent/`.
3. ~~**Hang test.**~~ **Done, 2026-09-10.** Embed an app, make it stop pumping messages, confirm the
   shell stays responsive with the out-of-process host — and confirm it does *not* without one.
   Both confirmed. It also found a second, undocumented freeze mechanism that affects attach mode
   too, and left the input-queue claim unmeasured. See
   [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md) and `spikes/03-hang-test/`.
4. ~~**cwd capture.**~~ **Done, 2026-09-10.** All three strategies measured across pwsh,
   powershell, cmd and WSL bash, over five scenarios each. Layered strategy succeeds 85% (34/40).
   **PowerShell's PEB is permanently stale and WSL's is meaningless**, so the shell snippets are
   mandatory, not optional. See [ADR 0004](docs/adr/0004-cwd-capture.md) and `spikes/04-cwd/`.

Write one short ADR per spike in `docs/adr/`. Record what failed, not just what worked.

## 8. Working agreements

- **Write the ADR when the decision is made**, not later. Short is fine — context, decision,
  consequences. Future sessions read these before changing direction.
- **The layout engine gets real unit tests.** Splits, ratios, focus movement, serialization
  round-trips. It is pure logic with no excuse for being untested, and everything else rests on it.
- **Keep `WinMux.Core` platform-free.** Enforced by test, not by discipline.
- **No silent failure around embedding or persistence.** Surface what happened and what the user
  can do. A pane that vanishes without explanation is worse than one that never opened.
- **Update this file when architecture changes.** It is the contract between sessions, and a stale
  contract is worse than none.
- **Update `HANDOFF.md` before the session ends.** See section 10. The contract says what was
  decided; the handoff says where the work stopped. Both or neither.
- Keep the README's honesty about limitations intact as the code grows. Overpromising on app
  compatibility is the fastest way to make this project look broken.

## 9. Open questions

- ~~Terminal rendering: adopt or write?~~ **Answered — adopt.** [ADR 0002](docs/adr/0002-terminal-stack.md).
- ~~Session file format: JSON or TOML?~~ **Answered — TOML.** [ADR 0006](docs/adr/0006-session-file-format.md).
- Detached/daemon sessions — does the shell survive its own restart with panes intact? Deferred
  past v1, but the process model should not make it impossible later.
- Adopting already-running apps (drag a running window into a pane) — v1 or later?
- Multi-monitor: one WinMux window per monitor, or one spanning window with per-monitor tabs?
- **Does input-queue attachment actually starve the shell of input?** Spike 3 could not see it
  (ADR 0001, finding 4). Needs a harness that synthesizes real input. Until then, chaining
  `SetParent` from the shell to a pane host stays forbidden.

## 10. Session handoff

Sessions are short and the project is long. `HANDOFF.md` at the repo root is how a new session
finds out where the last one stopped without replaying its transcript.

**Read it first, before doing anything.** Read it after `README.md` and this file, and treat it
as current fact — if it disagrees with what you find in the code, the handoff is stale and
fixing it is part of your work.

**Write it before you stop.** Not as a farewell note at the very end, but whenever the state of
play changes materially — a spike answered, a decision made, a direction abandoned.

It carries exactly five things, and stays under a page:

1. **Where we are** — the current phase, and the one sentence a newcomer needs.
2. **What just happened** — the last session's work, dated, with links to what it produced.
3. **The next action** — one concrete, specific thing. Not a backlog; the single next move.
4. **Blocked / needs a human** — decisions only the user can make, and what is waiting on them.
5. **Do not re-do** — approaches already tried and rejected, with the reason. This is the
   section that actually saves time; without it every session re-derives the same dead ends.

Rules that keep it useful:

- **It is a rolling snapshot, not a changelog.** Git is the changelog. Overwrite freely;
  a handoff that accumulates history stops being read.
- **Rewrite it, never append to it.** Appending is how it dies: a new paragraph on top, the old
  one left underneath, and a reader who cannot tell which is true.
- **One "Where we are".** If there are two, one of them is a lie.
- **Absolute dates**, never "yesterday" or "last session".
- **Link, do not restate.** Findings live in ADRs, architecture lives here. The handoff points.
- **Verify every number before writing it.** Test counts, commit counts and version numbers are
  exactly the facts a reader will trust without checking, so they are the ones worth checking.
- **Record what failed**, same as an ADR. A session that wasted two hours on a dead end has
  produced a real result; write it down.
- **If it is wrong, fix it before continuing.** A stale handoff is worse than an absent one,
  because it is trusted.

**This has already gone wrong once, on 2026-09-15.** Over a single long session the file was
appended to rather than rewritten, and reached 174 lines with three separate "latest" paragraphs, a
"next action" that had been done hours earlier, and a test count that was 54 short. Everything in
it was true when written and the file as a whole was misleading — which is the failure mode this
section exists to prevent, and it was not prevented by intending to prevent it.

So, concretely: **rewrite `HANDOFF.md` as part of the same change that makes it wrong.** A commit
that moves the project on and leaves the handoff describing the previous state is an incomplete
commit, in the same way that shipping a capability with no interface is an incomplete feature
(section 5a). If the rewrite feels like too much for a small change, that is the signal the file
has grown past a page and the fix is to cut it, not to append to it.
