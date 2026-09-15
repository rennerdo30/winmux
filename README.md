# WinMux

A window multiplexer for Windows. Think **tmux, but outside the terminal** — split panes,
tabs and saved sessions, where every pane is a real, independent OS process, and a pane can
be a shell, a file browser, or *any* Windows application.

> Status: **Phases 0–5 complete; the GUI (Phase 6) is most of the way through its first pass.**
> `WinMux.exe` continuously saves the layout and restore intent, while isolated PaneHost processes
> embed or attach foreign applications using measured compatibility rules. Any installed
> application can be put in a pane from a Start Menu picker, or adopted from a window that is
> already open. Broad app compatibility is still a measured surface, not a claim.
> See `HANDOFF.md` for the current state.

**[Documentation](https://rennerdo30.github.io/winmux/)** ·
[Getting started](https://rennerdo30.github.io/winmux/getting-started/) ·
[Releases](https://github.com/rennerdo30/winmux/releases) ·
[Architecture decisions](https://rennerdo30.github.io/winmux/architecture/)

---

## Why

Windows has four half-solutions and no whole one:

| Category | Examples | Panes | Process per pane | Non-terminal apps | Layout save |
|---|---|---|---|---|---|
| Terminal multiplexers | Windows Terminal, WezTerm, Zellij, Tabby | yes | yes | **no** — PTY only | partial |
| Tiling window managers | GlazeWM, komorebi, FancyWM, workspacer | desktop-level | yes | yes, but as loose desktop windows | weak |
| Window tabbers | Stardock Groupy, TidyTabs, WindowTabs | tabs only | yes | **yes** (real reparenting) | no |
| Layout restorers | PowerToys Workspaces | no | yes | yes | **yes** |

WinMux is the intersection: Groupy's embedding, tmux's split tree, Windows Terminal's ConPTY
shells, and Workspaces-grade persistence — in a single host window with one keymap.

## Core ideas

- **A pane is a process, not a widget.** Panes are hosted, not emulated. Killing WinMux does not
  have to kill your work; a wedged app cannot take the shell down with it.
- **The layout is a tree**, exactly like tmux: a window contains nested horizontal/vertical splits
  and stacks (tabs). Splits are resizable and the whole tree is persistable. Pane movement and tab
  reordering remain later interaction work.
- **Pane types are pluggable.** Terminal, file browser and foreign-app panes are three
  implementations of one interface. Adding a fourth should not touch the layout engine.
- **Sessions restore intent, not memory.** Reopening a session recreates the tree, the tabs, each
  pane's program, arguments and working directory. It does not resurrect process state — that is
  explicitly out of scope.

## Pane types

1. **Terminal** — PowerShell, cmd, WSL, pwsh, ssh. Backed by ConPTY, one child process each.
2. **File browser** — built in, native. Dual-pane friendly, with terminal handoff at the selected
   directory.
3. **Foreign app** — applications are embedded or attached through one isolated pane-host process
   per pane. The session can request `auto`, `embed`, or `attach`.
4. **Empty** — a pane you make first and fill afterwards. It offers your profiles, every
   application the Start Menu knows about, and the windows already open, which it can adopt.

A **profile** is a named thing you can put in a pane — a shell or an application, it makes no
difference to the layout. Profiles live in `%APPDATA%\WinMux\profiles.toml`, appear in every menu
that opens a pane, and are added from the installed-application list rather than by typing paths.

## Can it really host *any* Windows app?

No—not literally. Ordinary Win32 and Chromium windows worked in the Phase 0 measurements;
packaged/UWP windows require attach, while higher-integrity windows remain constrained by UIPI.
Two strategies are implemented:

- **Embed**: reparent the app's top-level window into the pane. True containment —
  it moves, clips and resizes with the layout.
- **Attach**: leave the window top-level and drive its position/size to track the pane, the way a
  tiling WM does. Less seamless, far more compatible. It is a *compatibility* fallback,
  not a stability one — measurement showed a wedged app stalls the host equally in both modes
  unless window calls are kept off the UI thread ([ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md)).

`strategy = 'auto'` is the session-file default for foreign apps. It consults the shipped,
user-editable `foreign-app-quirks.json`; unknown apps use an explicitly unverified embed default.
If embedding is refused, PaneHost keeps the same application alive, explains why, and switches it
to attach. `Ctrl+B`, then `A`, switches the focused foreign pane live and persists the result.

Known-hard cases, handled by falling back to *attach* or by refusing cleanly:

- **Elevated/higher-integrity apps** — UIPI can forbid both reparenting and positioning; failures
  are reported, and WinMux is never elevated as a workaround.
- **UWP / packaged WinUI apps** — they live under `ApplicationFrameHost`; reparenting is unreliable.
- **Apps with splash screens or multiple top-level windows** — need a per-app rule to pick the real one.
- **Mixed DPI** — the host enables mixed-mode DPI hosting, but different-scale monitors have not
  yet been available for the required physical multi-monitor verification.

Compatibility remains a measured product surface, not a claim that every application works.
See [ADR 0011](docs/adr/0011-phase-3-foreign-app-runtime.md) for exactly what was exercised.

## Cross-platform

The layout engine, session format, PTY contract and VT engine are platform-neutral by construction.
Only the Windows runtime is currently built and verified.
Foreign-window embedding is not, and this is a hard boundary rather than a backlog item:

| Platform | Terminal panes | Foreign-app embed |
|---|---|---|
| Windows 10 1809+ / 11 | primary target | yes (`SetParent`) |
| Linux / X11 | designed to be portable; unverified | future (`XReparentWindow`) |
| Linux / Wayland | designed to be portable; unverified | **impossible** — no foreign-surface reparenting |
| macOS | designed to be portable; unverified | **impossible** — no cross-process view embedding |

On Wayland and macOS, foreign-app panes degrade to separate windows or are unavailable. Windows is
the target; portability is a design discipline, not a promise.

## Non-goals

- Reviving process state across restarts (scrollback beyond a capped buffer, in-flight jobs, TUI state).
- Replacing your desktop window manager. WinMux owns its own window; it does not manage the shell.
- A remote/daemon detach-reattach model like `tmux -d`. Possibly later; not v1.
- Beating Windows Terminal at being Windows Terminal. Terminal panes need to be good, not novel.

## Roadmap

- **Phase 0 — spikes.** **Complete.** Four measured spikes settled the architecture.
- **Phase 1 — terminal multiplexer.** **Complete.** Split tree, tabs, ConPTY panes, keymap, palette, and CLI actions.
- **Phase 2 — persistence.** **Complete.** Atomic/debounced multi-window TOML, migration and
  validation, layered OSC/PEB cwd capture, profile onboarding, and visible crash/restore demo.
- **Phase 3 — foreign apps.** **Complete.** Embed/attach/live switching, isolated pane hosts,
  measured quirks selection, clean fallback, and a visible desktop acceptance script.
- **Phase 4 — file browser pane.** **Complete.** Pane types are providers behind a public
  contract, and the built-in file browser ships alongside terminal and foreign-app panes.
- **Phase 5 — portability.** **Layer extracted.** `WinMux.Platform` states what an operating
  system must provide and the shell no longer calls Windows itself. **Not proven on X11**, and it
  will not be until someone writes the X11 host — WinMux is a Windows product today, and keeping
  the seam honest is a discipline rather than a promise.
- **Phase 6 — the interface.** **In progress.** Nested tab groups with strips on any edge, a
  toolbar covering every layout operation, Mica and the system accent, profiles and the application
  catalogue, empty panes, window adoption, renaming, terminal scrollback and selection, settings.
  Still missing: tab and pane reordering, dragging a window into a pane, scrollback search.

## Run it

```powershell
.\run.cmd                                                   # build and launch
.\scripts\run.ps1 -Session .\examples\tabs-and-splits.toml  # nested tab groups
```

`run.cmd` and `publish.cmd` are double-clickable and need no execution-policy change.

Or by hand:

```powershell
dotnet build WinMux.slnx -c Release
.\WinMux.Shell\bin\x64\Release\net10.0-windows\WinMux.exe [session.toml]
```

For a folder you can copy elsewhere:

```powershell
.\publish.cmd                  # -> dist\WinMux-<version>-win-x64\ and a .zip
.\publish.cmd -SelfContained   # also carries the .NET runtime
```

To see the product phases rather than only run unit tests, use the visible walkthroughs:

```powershell
.\scripts\phase2-demo.ps1 -Configuration Release
.\scripts\phase3-demo.ps1 -Configuration Release
```

Every layout operation is a toolbar button, so none of this has to be memorised — but the default
keymap is tmux-style: `Ctrl+B`, then `%`/`"` to split, arrows to move focus,
`Shift+arrows` to resize, `c` for a tab, `v` for a tab group with its tabs down the side,
`n`/`p` to cycle, `,` to rename a pane, `e` for an empty pane, `x` to close, `w` to save, `A` to
toggle a foreign pane between embed and attach, and `:` for
the command palette. `1`–`4` open cmd, Windows PowerShell, PowerShell 7, or WSL profiles; `i` opens
cwd-reporting setup. Pass
`--no-prefix`, or `--keymap path.json`, to replace the default map. `winmux help` lists the CLI
action surface.

## Prior art worth reading

- [Stardock Groupy](https://www.stardock.com/products/groupy/) — proof that arbitrary-app embedding ships.
- [PowerToys Workspaces](https://microsoft.github.io/PowerToys/modules/workspaces/) — the persistence bar to clear.
- [Windows Terminal panes](https://learn.microsoft.com/en-us/windows/terminal/panes) and
  [startup/session settings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/startup).
- [komorebi](https://github.com/lgug2z/komorebi), [GlazeWM](https://glazewm.com/),
  [FancyWM](https://github.com/FancyWM/fancywm) — Windows window-manipulation techniques.
- [Raymond Chen on cross-process parent/child windows](https://devblogs.microsoft.com/oldnewthing/20130412-00/?p=4683)
  — read this before writing any embedding code.

## License

[MIT](LICENSE).
