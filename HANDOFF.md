# HANDOFF — WinMux

## Where we are

Phases 0–5 are complete; Phase 6 features are implemented, with runtime acceptance still open.
Source version: `0.7.8-test.5`. Optional rendering preference and terminal/tab interaction fixes
are implemented; no release was published. See [ADR 0026](docs/adr/0026-optional-gpu-rendering.md)
and [ADR 0027](docs/adr/0027-terminal-and-tab-interaction.md).

## What just happened

**2026-10-07:** Added Automatic (ANGLE then Software) and forced Software rendering at startup,
with persisted Settings controls. Avalonia supplies the drawing path; GPU-first was already its
default, so this establishes mode selection, not a measured speedup.
Changed the default prefix to Ctrl+Shift+P, preserving custom maps and freeing Ctrl+B. Added
application wheel reports/alternate scroll and Shift-wheel history; query replies preserve history.
Output/title work yields to input, tab buttons/menus survive title refreshes, and hidden focus
events cannot reset nested selection. Pin clicks require an explicit Unpin menu. Removed the
duplicate toolbar separator. Fresh process-tree checks ask before closing busy terminals/the window.
Release solution build with SDK 10.0.400: 0 warnings/errors. Full suite: 1,198 passed, 6 live FTP/SFTP
skips, 0 failures. Real-control fake-PTY tests cover wheel, query replies and flood-time Ctrl+B;
Skia captures verify separator uniformity at 100%/150%. Native acceptance remains open.
Pinned SDK: `%LOCALAPPDATA%\WinMuxDev\dotnet\dotnet.exe`. Review executable:
`artifacts/review/bin/WinMux.Shell/release/WinMux.exe` (ordinary output was locked by the running app).
Standing constraints remain in [development guardrails](docs/development-guardrails.md).

## The next action

Run the review executable and check Claude Code wheel/Ctrl+B, tab switching and rename during
sustained output, nested selection, pin/unpin and busy versus idle terminal close confirmation.

## Blocked / needs a human

Native Claude Code compatibility, sustained-output feel, Windows frame/corners, native GPU execution,
driver-failure fallback, VM behavior and mixed-DPI acceptance are unmeasured. Shell builtins without
child processes cannot be detected as busy; WSL is protected conservatively. Stable release remains
open. Connection-source follow-ups: RDCMan file selection and WinSCP registry editing
([ADR 0025](docs/adr/0025-foreign-connection-sources.md)).

## Do not re-do

Read development guardrails and CLAUDE.md before changing embedding, persistence, rendering or focus.
Do not call foreign HWNDs on the UI thread, hard-kill hosts still owning children, or claim tests
prove native GPU/VM performance. Keep software fallback and load preferences before App.Initialize.
Coalescing at Render still starves input; repaint/title/state work must yield to Input. Keep retained
tab controls across metadata refreshes and Core active-leaf memory for nested groups.
