# ADR 0041 — Separate session restore from folder-open intent

- **Status:** accepted; native acceptance open
- **Date:** 2026-10-22
- **Issue:** WM-20

## Context

ADR 0029 and `ec6a595` made restored default startup enqueue the caller's directory.
That inserts a CMD tab on every ordinary restart and autosave keeps it. Persistence
has priority over folder-launch convenience. A working directory or parent process
does not establish whether the user requested a folder.

## Decision

Ordinary primary startup restores the saved layout without enqueueing CMD. Existing
panes, including CMD panes saved by earlier versions, remain untouched. A bare
secondary invocation still forwards its captured caller directory to the active window.

`WinMux.exe --open-directory <directory>` expresses folder intent on cold or warm
startup. Relative folders resolve against the caller directory. A restored primary
queues exactly one request through the existing launch service and readiness barrier.
With no saved session, the normal two-pane default layout (CMD and file browser) uses
the requested folder for both panes and queues nothing: exactly one CMD in total.
The wire protocol and per-user launch ownership are unchanged.

A positional session cannot be combined with `--open-directory`; multiple positional
sessions and repeated folder options are errors. Keymap and no-prefix options keep
their primary-startup behavior, including with a folder option. A secondary refuses
session/keymap/no-prefix options rather than silently discarding them.

Validate folder syntax and availability before launch ownership, session migration,
settings, update-result collection or crash-log installation. Missing, empty, malformed
and unavailable folders fail clearly; they do not fall through to the default session.
CMD cannot use UNC working directories, so explicit UNC requests are refused; use a
mapped drive. No session migration, pane cleanup or origin inference is introduced.

## What failed

The ADR 0029 rule conflated ordinary restoration with a new folder request. Lower-level
launch tests correctly proved that requests add CMD, but never checked whether ordinary
startup should produce a request. Testing only an isolated argument helper would leave
that wiring mistake undetected. The regression exercises Program's production startup
queue with the actual SessionController/MainWindow path and fake pane providers.

## Validation and limits

On base `02a6826502ec2f01a758b7dcc396204067c83e57`, extracting the original queue
condition unchanged and adding the regression produced **1 failed / 0 passed**: the
original PowerShell pane gained an unwanted CMD. After the fix, **55 focused tests
passed, none failed or skipped** (`StartupArgumentsTests`, `LaunchDirectoryIntegrationTests`,
`SingleInstanceLaunchTests`, `KeymapTests`). The solution Release compile completed with
zero warnings/errors using the pinned SDK and cached NuGet packages. Logs and TRX files
are in the job worktree's ignored `artifacts/wm20/`; WM-20 carries the evidence summary.

Tests use isolated temporary sessions, fake runtimes and headless windows without real
shells, registry or user settings. Native Explorer/foreground behavior and full-suite CI
remain parent gates. This proves one unwanted addition per ordinary restored launch;
the owner's observation of an addition in every tab group remains unmeasured.
