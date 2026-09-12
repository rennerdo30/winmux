# HANDOFF

Rolling snapshot of where the work stands. Rules: `AGENTS.md` / `CLAUDE.md` section 10.

**Last updated:** 2026-09-12

## Where we are

**Phase 2 is complete.** WinMux is a persistent terminal multiplexer: it continuously saves every
window, layout/tab, process launch descriptor, and layered cwd to crash-safe TOML, then recreates
that intent with visible failure handling. See
[ADR 0010](docs/adr/0010-phase-2-persistence-runtime.md).

| Project | State |
|---|---|
| `WinMux.Core` | platform-free layout/session model; validated, migrating, atomic TOML; debounced autosave |
| `WinMux.Pty` / `WinMux.Terminal` | owned ConPTY/VT seams; Windows argument quoting; OSC 9;9 / OSC 7 cwd events |
| `WinMux.Shell` | multi-window restore, OSC + deepest/root PEB cwd layers, profile installer/onboarding, visible errors |
| `WinMux.Cli` | stable named actions sent to the active WinMux window |
| `WinMux.PaneHost` | early Phase 3 isolated foreign-window host; detach/close stream IPC |

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`.
Current result: **zero build warnings; 186/186 tests passing**. The visible crash/relaunch
acceptance is `scripts/phase2-demo.ps1`.

## What just happened

**2026-09-12** — Phase 2 closed.

- Added serialized debounced autosave, including a mutation arriving during an in-flight write.
  Session replacement uses unique sibling files, durable flush, concurrent-writer retry, and
  preserves the previous file on failure.
- Added v0→v1 migration, strict cwd provenance/timestamps, and refusal of duplicate/unreachable
  structure. GUI startup quarantines malformed TOML and shows the error instead of starting empty.
- Wired streaming OSC cwd reports into live panes and added x64 deepest-child/root PEB fallback.
  A fresh nested-child capture can supersede stale OSC; WSL is explicitly OSC-only and restores
  Linux cwd through `wsl.exe --cd`.
- Embedded the measured PowerShell/cmd/bash reporters. Startup warns once per missing shell;
  Windows installation is one-click, backed up, and idempotent, while WSL remains an explicit
  manual step. `configure-cwd-reporting` / prefix `i` reopens setup.
- Restored all top-level windows, titles and clamped geometry. Terminal programs are normalized to
  absolute paths; args, env and cwd are proven against a real restored cmd process. The UI explains
  that jobs and in-memory TUI state are recreated, not resumed.
- Added a two-window forced-crash demo and scale/concurrency coverage. A real-process test exposed
  Porta.Pty's POSIX quoting on Windows and a `conhost.exe` cwd false positive; WinMux now owns
  CreateProcess-compatible quoting and excludes console infrastructure from PEB candidates.

## The next action

**Start Phase 3 runtime compatibility:** wire the measured quirks seed into per-app strategy
selection, implement attach fallback without putting foreign HWND calls on the UI thread, and add
one measured acceptance case for a packaged/UWP app that embed mode must refuse or attach.

## Blocked / needs a human

- **Mixed-DPI multi-monitor remains unmeasured.** Both available monitors were 144 DPI; Phase 2
  clamps saved geometry but cannot claim mixed-scale fidelity.
- **Supply-chain call before v1:** `Terminal.Emulation` 0.3.3 has no public source. The owned seam
  bounds replacement cost but does not answer whether to ship it.
- **Input-queue starvation remains unmeasured.** Keep PaneHost top-level until a real-input harness
  answers it; do not reparent PaneHost into the shell.

## Do not re-do

- Do not hand-roll ConPTY; the silent inherited-stdio failure is in
  [ADR 0002](docs/adr/0002-terminal-stack.md).
- Do not rank every OSC report over a newer deepest-child PEB; nested non-cooperating shells are
  the measured counterexample in [ADR 0004](docs/adr/0004-cwd-capture.md).
- Do not rely on Porta.Pty's default Windows argument quoting; multiword arguments arrive with a
  literal POSIX quote. The owned PTY seam quotes them.
- Do not call a foreign HWND from the UI thread or hard-kill a host that still owns a child; see
  [ADR 0008](docs/adr/0008-pane-host-ipc.md).
- Do not put a palette or modal over the pane canvas; native windows paint above it.
- `E:\Development\winmux` and `D:\Development\winmux` are the same project via subst/junction.
- NuGet restore and Avalonia builds may require sandbox approval for user-local config/telemetry.
