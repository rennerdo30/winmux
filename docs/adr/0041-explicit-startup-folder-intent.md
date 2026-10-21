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

Crash logging belongs only to the accepted primary path, after secondary argument
validation and forwarding have returned. Secondary invocations never install the
process-exit writer, including when a valid folder is combined with primary-only options.

## What failed

The ADR 0029 rule conflated ordinary restoration with a new folder request. Lower-level
launch tests correctly proved that requests add CMD, but never checked whether ordinary
startup should produce a request. Testing only an isolated argument helper would leave
that wiring mistake undetected. The regression exercises Program's production startup
queue with the actual SessionController/MainWindow path and fake pane providers.

Review found a second ordering gap: parsing accepted a folder plus `--no-prefix`,
then the crash-log hook was installed before secondary validation returned 64.
That hook could create, append or rotate `crash.log` at process exit. Helper-only
argument tests could not detect this; the startup-order regression runs the same
orchestration called by Main, substituting the process hook, native notifier and
an isolated launch endpoint. The crash-log installer must never be called on warm
paths; tests do not install real process-wide hooks or access the user's profile.

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

Review correction on `96ac00041d18ddc3f77a2e0508ce82696d9388ff`: the unchanged
pre-validation installation order, with only test wiring extracted, produced
**8 failed / 0 passed** in `StartupOrderingTests` (`ordering-red-tests.log`,
`wm20-ordering-red.trx`). All eight detected one installer call instead of zero;
six rejected warm combinations still returned 64 and forwarded nothing. Moving
installation to the primary path produced **63 passed / 0 failed / 0 skipped**:
the earlier 55 assertions plus eight production-order cases, including successful
bare and explicit warm forwarding exactly once (`ordering-green-focused.log`,
`wm20-ordering-green.trx`). The changed Shell.Tests dependency batch compiled
offline with cached assets, `--no-restore`, zero warnings/errors
(`ordering-green-build.log`). Logs identify the source base plus working-tree
changes on their first line; `ordering-evidence.txt` records commands and hashes.
No full solution rebuild or local full suite was repeated. Actual process exit,
native UI/Explorer, registry, settings and real shell launches were NOT RUN;
full CI reruns through the existing PR after the correction is pushed.
