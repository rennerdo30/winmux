# ADR 0009 — Phase 1 terminal runtime and named actions

- **Status:** accepted
- **Date:** 2026-09-12
- **Code:** `WinMux.Pty`, `WinMux.Terminal`, `WinMux.Shell`, `WinMux.Cli`

## Context

The first runnable shell proved the layout with `Terminal.Avalonia`, but it bypassed the owned
interfaces required by ADR 0002 and hard-coded its key handling in `MainWindow`. Phase 1 also
requires usable split/tab interaction and one action vocabulary shared by keys, a palette, and the
CLI.

## Decision

1. `WinMux.Pty` owns process lifecycle behind `IPtySession` and adapts pinned `Porta.Pty` 2.2.2.
2. `WinMux.Terminal` owns the VT grid behind `ITerminalEngine` and adapts pinned
   `Terminal.Emulation` 0.3.3. Dependency types do not appear in either public contract.
3. `TerminalPaneControl` composes those two contracts, pumps bytes in both directions, renders the
   grid, propagates character-cell resizing, handles terminal input, and provides basic full-screen
   copy plus paste. `WinMux.Shell` no longer references `Terminal.Avalonia` or `Terminal.Pty`.
4. Stable action names live in one `ActionDispatcher`. The configurable binding table uses a
   tmux-style prefix by default, supports direct/no-prefix maps, and can load JSON via `--keymap`.
5. The command palette is a separate top-level window so hosted native panes cannot occlude it.
   The CLI invokes the same action names through a current-user-only named pipe.
6. Phase 1 UI includes clickable tabs, directional focus, draggable dividers, keyboard resizing,
   inherited terminal launches, and named cmd, Windows PowerShell, PowerShell 7, and WSL profiles.

## Consequences

- The product terminal path now exercises the replaceable seams, not only spike code.
- CLI commands require one running shell for the current user. A second shell reports that its CLI
  listener is unavailable instead of silently losing commands.
- Rendering is deliberately modest: fixed metrics, one text run per row, no selection model, and
  no scrollback navigation yet. Those are quality improvements, not reasons to bypass the seam.
- A generic `winmux action NAME` keeps new named actions scriptable without adding a bespoke CLI
  command, while common actions retain discoverable aliases.

## Verification

On 2026-09-12 the solution built with zero warnings. The normal solution test gate covered the
layout/session suite, keymap and dispatcher, named-pipe client/server, a real cmd PTY round trip and
resize, VT grid/reflow/wide-glyph/hyperlink behavior, and dependency-boundary checks. A desktop run
then invoked split, tab, and save through the CLI, kept the shell responsive, and exited cleanly
through `WM_CLOSE` with no pane hosts left behind.

## What failed

- The first product terminal used `Terminal.Avalonia` directly. It rendered, but that success hid
  the exact dependency lock-in ADR 0002 required us to avoid; the package has no public source.
- The hard-coded `MainWindow` key switch could not serve no-prefix users and gave the palette and
  CLI no shared action surface.
- The first cross-project command-channel test referenced both executables, whose assembly names
  differ only by case (`WinMux` and `winmux`). MSBuild treated that as an identity collision. The
  server and client are now tested from separate projects against the protocol boundary.
- A focused terminal consumed `Ctrl+B` before a bubbling window handler saw it. Shell key routing
  now tunnels first; unhandled keys continue to the terminal.
