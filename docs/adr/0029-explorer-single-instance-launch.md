# ADR 0029 — Explorer launches and one WinMux process per user

- **Status:** accepted; native Explorer acceptance open
- **Date:** 2026-10-07
- **Spike:** none

## Context

The user wants to type winmux in Explorer's address bar and open CMD in that folder. With WinMux
already running, this must add a tab to its active window rather than start a second process or
change the existing session. The Explorer/current-directory behavior is the user's stated premise.

## Decision

Capture the caller's working directory before startup work. A named per-user ownership marker
selects the primary before session loading/migration or autosave. A dedicated CurrentUserOnly
named pipe carries bounded, length-prefixed UTF-8 JSON launch requests; wmux's command pipe stays
unchanged. The listener starts before restore. Requests wait in a bounded serial queue until the
application installs its handler, which marshals to the UI and waits for pane restoration before
creating CMD with the explicit directory. Success is acknowledged only after tab creation.
Missing folders, handler/startup failure and timeout return visible explanations. A timeout after
delivery may still result in a tab; no duplicate instance or automatic resend is attempted.

A restored default startup also adds CMD at the caller's folder. A fresh default layout already
contains that CMD, so it adds no duplicate. Explicit session/keymap arguments keep their existing
primary-startup behavior; a secondary rejects them visibly rather than discarding them or replacing
the user's session. A read-only legacy command-pipe probe can detect older running versions and
asks for a safe restart. It never sends a command or closes the older process.

Normal GUI startup registers its current WinMux.exe in HKCU App Paths through a platform interface,
so Explorer/Run can locate a portable installation without changing PATH or machine registry.
Moved locations refresh on the next direct launch; unchanged registrations skip writes. Testhost,
dotnet and headless processes skip registration. Registration failures become actionable startup
notices. See Microsoft's [Application Registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration).

## Validation and limits

Solution Release build has zero warnings/errors; 1,258 tests pass and six live FTP/SFTP tests skip.
Named-pipe tests cover secondary delivery/fulfilled acknowledgement, cold readiness, shutdown,
timeout without duplicate, malformed clients and subsequent recovery, validation and ownership.
Actual MainWindow/SessionController headless tests inject fake providers: distinct caller folders
start CMD tabs, preserve existing pwsh panes and route to the active window after startup readiness.
Fake registry tests cover registration, moved paths, failure notices and process identity gating.
Tests never launch user processes, show MainWindow or mutate the real registry.

Native Explorer invocation, OS foreground activation and packaged application acceptance still
need an interactive check after safely restarting the older running WinMux. The legacy probe is
bounded and detects connectable older listeners; it cannot establish older-instance absence when
their pipe is busy or inaccessible. Native elevated/desktop boundary behavior remains unmeasured.

## What failed

The existing command channel carried only action names, so a new-terminal action inherited the
focused pane's directory and could not carry Explorer's folder. Starting another shell process
contended for the command pipe and could load/write the same session independently. Restoring the
default session ignored a new caller's directory. Treating a queued request as immediate success
would hide later startup or pane-creation failures. A running older version has no launch channel,
so it needs a safe restart rather than an attempted forced upgrade or termination.
