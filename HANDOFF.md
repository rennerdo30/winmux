# HANDOFF — WinMux

## Where we are

Phases 0–5 complete; Phase 6 implemented with native acceptance still open. Source version:
`0.7.8-test.8`; [test.8 is published](https://github.com/rennerdo30/winmux/releases/tag/v0.7.8-test.8)
and its downloaded ZIP checksum is verified.
New work: Explorer folder launches route CMD into one WinMux process per user.
See [ADR 0029](docs/adr/0029-explorer-single-instance-launch.md); earlier rendering/tab/key policies
remain in ADRs [0026](docs/adr/0026-optional-gpu-rendering.md),
[0027](docs/adr/0027-terminal-and-tab-interaction.md), [0028](docs/adr/0028-tab-width-and-terminal-key-ownership.md).

## What just happened

**2026-10-07:** Added early per-user launch ownership and a separate CurrentUserOnly pipe carrying
explicit caller folders. Second launches queue until restoration, then open CMD in the active
window; success waits for pane creation. Existing layout/session stays in use. Fresh default
startup already has its caller-folder CMD; restored default startup adds one. Secondary session/
keymap arguments reject visibly; older running versions need a safe restart. No processes killed.
Normal GUI startup registers its current WinMux.exe in per-user App Paths via a platform interface,
so Explorer/Run can find it without changing PATH. Moves refresh on direct launch; failures notify.
Release solution build: 0 warnings/errors. Full suite: 1,258 passed, six live FTP/SFTP skips,
zero failures. IPC tests exercise startup/shutdown/timeout/validation; actual headless window tests
use fake providers for CMD cwd, repeated requests and active-window routing. Registration uses fake
registry tests. No native GUI launches or real registry changes were performed during validation.
Test.8 folder/zip passed packaging checks, component versions and packaged CLI validation;
GitHub release workflow passed. Separate main CI exposed a pre-existing autosave test's fixed
150 ms wait; it now awaits SaveCompleted with a bounded deadline. All five autosave tests pass
locally; follow-up main CI pending. SDK: `%LOCALAPPDATA%\WinMuxDev\dotnet\dotnet.exe`.
Standing constraints: [development guardrails](docs/development-guardrails.md).

## The next action

Safely close the older WinMux, run test.8 WinMux.exe once to register it, then type winmux from
several Explorer folders and verify one process, new active CMD tabs and the expected directories.

## Blocked / needs a human

Native Explorer/foreground activation and elevated desktop boundaries remain unmeasured. The
bounded legacy pipe probe detects connectable older instances, not busy/inaccessible listeners.
Prior native Windows drag/DPI feel, Claude controls, GPU/fallback/VM and mixed-DPI acceptance
remain open; GPU-first was already Avalonia's default, with no measured speedup. Shell builtins
without children cannot be detected busy; WSL protected conservatively. Stable release remains open.
Connection-source follow-ups: RDCMan file selection/WinSCP registry editing ([ADR 0025](docs/adr/0025-foreign-connection-sources.md)).

## Do not re-do

Keep default sessions in AppData and never replace them during a folder launch. Ownership precedes
session loading; retain bounded launch queue, fulfilled ACKs and no duplicate on timeout. No real
registry writes in tests. Read guardrails/CLAUDE.md before embedding, persistence or focus changes.
Retain software fallback, Background output dispatch, stable tab controls and unchanged lockfiles.
