# ADR 0010 — Phase 2 persistence runtime and cwd recovery

- **Status:** accepted
- **Date:** 2026-09-12
- **Code:** `WinMux.Core`, `WinMux.Terminal`, `WinMux.Pty`, `WinMux.Shell`

## Context

ADR 0006 settled the editable TOML shape and ADR 0004 measured the cwd strategies, but the
product still saved one window only when explicitly asked and persisted mostly launch-time cwd
values. Phase 2 has to preserve the latest restorable intent after a crash, across every window,
without pretending that process memory can be revived.

## Decision

1. One `SessionController` owns all top-level windows and one named-action server. Layout, title,
   geometry, and cwd changes feed a 500 ms `SessionAutosaver`; writes are serialized and an update
   arriving during an in-flight write is flushed before shutdown returns.
2. `SessionFile` writes a unique same-directory temporary file with write-through and durable
   flush, then atomically replaces the destination. Concurrent writers serialize the rename and
   a failed replace leaves the previous bytes intact. Malformed input is quarantined and refused.
3. Version 0 is the executable migration fixture: a legacy cwd without metadata becomes
   `LaunchDirectory` at Unix epoch. Version 1 requires both provenance and capture time. Missing
   nodes, duplicate pane IDs, unreachable focus, cycles, and orphaned nodes are errors, not guesses.
4. The terminal engine recognizes bounded streaming OSC 9;9 and OSC 7 reports. Product panes save
   those immediately with `ShellReported` provenance. Every ten seconds and before explicit save
   or shutdown, Windows panes query the deepest process PEB then the root PEB. A newer deepest-child
   capture may supersede an older shell report (the measured nested-shell case); a root PEB never
   supersedes PowerShell's shell report. WSL is OSC-only.
5. The measured PowerShell, cmd, and bash/zsh snippets are embedded product assets. A startup
   window warns once per missing shell and offers one-click, backed-up, idempotent installation for
   Windows profiles; WSL receives the exact manual command. The command palette can reopen setup.
6. Terminal executable names are resolved to absolute paths before persistence when possible.
   Restored Windows and WSL cwd, arguments, and environment overrides are passed to the recreated
   process. WSL uses `wsl.exe --cd` for a persisted Linux path.
7. Every saved top-level window is recreated with its title and bounds. Bounds are clamped to an
   available working area when the monitor topology changed. The UI states plainly that processes
   are recreated and in-flight jobs/TUI memory are not restored. Load, save, launch, cwd fallback,
   and profile-install failures are visible.

## Consequences

- A crash loses at most the debounce interval for layout/OSC changes and the ten-second sampling
  interval for a non-cooperating shell's PEB-only cwd.
- PowerShell accuracy still depends on its profile integration; the UI no longer hides that fact.
- A process path that cannot be resolved remains visible as an error and is retained for manual
  repair instead of being silently replaced.
- Mixed-DPI placement remains unmeasured. Logical pane layout is platform-free; the Windows shell
  alone owns screen coordinates and PEB inspection.

## Verification

The Phase 2 gate covers fragmented/malformed OSC, a real cmd PEB query, WSL PEB refusal, profile
backup/idempotence, v0 migration, malformed-file quarantine, concurrent/failing atomic writes,
in-flight autosave mutation, multi-window geometry clamping, an eight-window/512-pane round trip,
and a real process launched from TOML-restored program/arguments/environment/cwd. The visible
two-window crash/relaunch walkthrough is `scripts/phase2-demo.ps1`. On 2026-09-12 the Release
solution built with zero warnings and all 186 tests passed.

## What failed

- The pre-Phase-2 shell loaded and saved only `Windows[0]`; the format supported multiple windows
  while the product silently discarded the rest.
- A fixed `session.toml.tmp` allowed concurrent writers to collide, and ordinary buffered writes
  did not prove the replacement bytes reached disk.
- The first autosaver could return from a flush after saving an older snapshot when a mutation
  arrived during the filesystem write. Version tracking now forces the latest snapshot through.
- Trusting source rank alone kept an old OSC report over a live nested-shell PEB. Comparing capture
  time for the OSC/deepest-child pair preserves the complementary behavior measured in ADR 0004.
- Porta.Pty 2.2.2 quoted multiword Windows arguments with POSIX single quotes. `cmd.exe` treated the
  quote as part of the command; WinMux now owns CreateProcess-compatible quoting at its PTY seam.
- The first deepest-process tie-break sometimes chose `conhost.exe` beside the real workload and
  persisted its plausible `C:\Windows` cwd. Console infrastructure is now excluded before the
  deepest-child/root fallback is selected.
- A malformed focused-pane ID was previously replaced with the first pane by `LayoutTree`. Session
  validation now rejects it before that convenience fallback can erase evidence of file damage.
