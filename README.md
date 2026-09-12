# WinMux

A window multiplexer for Windows. Think **tmux, but outside the terminal** — split panes,
tabs and saved sessions, where every pane is a real, independent OS process, and a pane can
be a shell, a file browser, or *any* Windows application.

> Status: **Phase 1 complete — terminal multiplexer.** `WinMux.exe` runs ConPTY terminals in a
> split/tab layout with a configurable keymap, command palette, and CLI actions. Persistence and
> foreign-app embedding have working early implementations; file panes and broad compatibility do
> not. See `HANDOFF.md` for the measured state and remaining work.

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
2. **File browser** *(planned)* — built in, native. Dual-pane friendly, with terminal handoff at the
   selected directory.
3. **Foreign app** *(early implementation)* — ordinary Win32 applications can be launched and
   embedded through an isolated pane-host process.

## Can it really host *any* Windows app?

No—not literally, and the current product path implements **embed only**. Ordinary Win32 and
Chromium windows worked in the Phase 0 measurements; elevated and packaged/UWP windows did not.
Two strategies are part of the design:

- **Embed** (default): reparent the app's top-level window into the pane. True containment —
  it moves, clips and resizes with the layout.
- **Attach** *(future compatibility fallback)*: leave the window top-level and drive its position/size to track the pane,
  the way a tiling WM does. Less seamless, far more compatible. It is a *compatibility* fallback,
  not a stability one — measurement showed a wedged app stalls the host equally in both modes
  unless window calls are kept off the UI thread ([ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md)).

Known-hard cases, handled by falling back to *attach* or by refusing cleanly:

- **Elevated apps** — UIPI forbids a non-elevated host from reparenting them.
- **UWP / packaged WinUI apps** — they live under `ApplicationFrameHost`; reparenting is unreliable.
- **Apps with splash screens or multiple top-level windows** — need a per-app rule to pick the real one.
- **Mixed DPI** — hosting a differently-DPI-aware app requires explicit mixed-mode hosting.

The measured quirks seed exists, but it is not wired into runtime selection yet. Compatibility is
a product surface still under construction, not a solved problem.

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
- **Phase 2 — persistence.** In progress: TOML save/restore works; live cwd capture/profile installation remains.
- **Phase 3 — foreign apps.** Embed and attach modes, out-of-process pane hosts, quirks database.
- **Phase 4 — file browser pane** and the public pane-provider interface.
- **Phase 5 — portability.** Extract the platform layer, prove it on X11.

## Run it

```powershell
dotnet build WinMux.slnx -c Release
.\WinMux.Shell\bin\x64\Release\net10.0-windows\WinMux.exe [session.toml]
```

The default keymap is tmux-style: `Ctrl+B`, then `%`/`"` to split, arrows to move focus,
`Shift+arrows` to resize, `c` for a tab, `n`/`p` to cycle, `x` to close, `w` to save, and `:` for
the command palette. `1`–`4` open cmd, Windows PowerShell, PowerShell 7, or WSL profiles. Pass
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
