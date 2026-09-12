# HANDOFF

Rolling snapshot of where the work stands. Rules: `AGENTS.md` / `CLAUDE.md` section 10.

**Last updated:** 2026-09-12

## Where we are

**Phase 1 is complete.** WinMux is a usable terminal multiplexer: owned ConPTY sessions and VT
engine, resizable split tree, visible tabs, shared named actions, configurable tmux/no-prefix
keymaps, a top-level command palette, CLI control, and terminal profiles. Early Phase 2 persistence
and Phase 3 out-of-process foreign-app embedding also run, but are not complete phases.

| Project | State |
|---|---|
| `WinMux.Core` | platform-free layout tree, pane/session model, atomic TOML persistence |
| `WinMux.Pty` | `Porta.Pty` behind `IPtySession`; real cmd lifecycle/resize test |
| `WinMux.Terminal` | `Terminal.Emulation` behind `ITerminalEngine`; headless grid tests |
| `WinMux.Shell` | owned terminal renderer, splits/dividers/tabs, profiles, palette, command server |
| `WinMux.Cli` | session commands plus named actions sent to a running shell |
| `WinMux.PaneHost` | one top-level isolated host per foreign pane; detach/close stream IPC |

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`. Current
result: **zero build warnings; 130/130 tests passing**. Decisions:
[ADR 0008](docs/adr/0008-pane-host-ipc.md) and
[ADR 0009](docs/adr/0009-phase-1-terminal-runtime.md).

## What just happened

**2026-09-12** — Phase 1 closed.

- Replaced the direct `Terminal.Avalonia` runtime with `WinMux.Pty` + `WinMux.Terminal` owned
  contracts and a small Avalonia renderer. Input, VT responses, resize, title, exit, paste, and
  basic full-screen copy pass through those seams.
- Replaced the hard-coded key switch with stable action names and one configurable binding table.
  `--no-prefix` and `--keymap FILE` work; defaults remain `Ctrl+B`-prefixed.
- Added clickable tab chrome, draggable dividers, keyboard resize, direct tab selection, a
  top-level command palette, and cmd / Windows PowerShell / PowerShell 7 / WSL profiles.
- Added a current-user named-pipe action channel and CLI aliases (`split`, `focus`, `resize`, tabs,
  close, save, palette) plus generic `winmux action NAME`.
- Moved foreign embedding into `WinMux.PaneHost`. Character Map detach restored its original HWND;
  the full shell/host/app run stayed responsive and restored the app on shell close.
- Final desktop acceptance created, persisted, and closed five cmd panes via CLI actions:
  shell responsive, all five child processes cleaned up, exit code 0, no PaneHost left behind.
- Updated README and architecture text to remove stale Phase 0/direct-host claims.

Run:

```powershell
dotnet build WinMux.slnx -c Release
.\WinMux.Shell\bin\x64\Release\net10.0-windows\WinMux.exe [session.toml]
```

Default prefix actions: `%` / `"` split, arrows focus, Shift+arrows resize, `x` close, `c` tab,
`n` / `p` cycle tabs, `w` save, `:` palette, `1`–`4` terminal profiles.

## The next action

**Start Phase 2 with live cwd capture:** parse OSC 9;9 / OSC 7 in the product terminal path, update
each pane's `WorkingDirectory` with `ShellReported` provenance, and add a round-trip test that saves
the changed cwd and restores the same launch directory. The measured snippets to ship are in
`spikes/04-cwd/profiles/`; see [ADR 0004](docs/adr/0004-cwd-capture.md).

## Blocked / needs a human

- **Mixed-DPI multi-monitor remains unmeasured.** Both available monitors were 144 DPI.
- **Supply-chain call before v1:** `Terminal.Emulation` 0.3.3 has no public source. The owned seam
  keeps replacement bounded but does not remove the decision.
- **Input-queue starvation is still unmeasured.** Keep PaneHost top-level until a real-input harness
  answers it; do not reparent PaneHost into the shell.

## Do not re-do

- Do not hand-roll ConPTY; the silent inherited-stdio failure is recorded in
  [ADR 0002](docs/adr/0002-terminal-stack.md).
- Do not call a foreign HWND from the UI thread or hard-kill a host that still owns a child; see
  [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md) and [ADR 0008](docs/adr/0008-pane-host-ipc.md).
- Do not put a command palette over the pane canvas; native windows paint above it.
- Direct `NativeControlHost` embedding rendered but violated the process boundary; ADR 0007 is
  superseded by ADR 0008.
- Do not treat a passing harness as proof until its failure mode is mutation-tested. Prior false
  passes (ConPTY output, resize echo, DPI, malformed TOML) are documented in the ADRs.
- `E:\Development\winmux` and `D:\Development\winmux` are the same project via subst/junction.
- NuGet restore reads the user config; Avalonia build writes a user-local telemetry log, so a
  restricted sandbox may require approval for otherwise ordinary restore/build commands.
