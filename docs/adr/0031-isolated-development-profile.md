# ADR 0031 — Isolated development profile

**Date:** 2026-10-12  
**Status:** Accepted

## Context

A checkout run shared settings, session, crash log, launch mutex, command pipe and App Paths with an
installed WinMux. Tests even appended simulated failures to the user's crash log. A development run
could therefore change or redirect the installed application without intending to.

## Decision

`WINMUX_DATA_DIR` selects one isolated data identity for the current process. Settings, profiles,
session, crash and update logs use that directory; an override of the foreign-app quirks file is
read there when present. A hash of its canonical path separates the launch mutex and command pipe.
The CLI reads the same variable. An isolated run skips App Paths registration, the legacy-instance
probe and session migration from the checkout. `scripts/run.ps1` gives each checkout an ignored
`artifacts/dev-data` directory and scopes the variable to the launched process. The shell test
assembly sets a unique process-local directory before tests run.

The unset variable retains the existing installed paths and endpoint names. An explicitly named
session file still wins, including in an isolated run.

## Verification

The shell tests check data paths, command-pipe routing and crash-log version. A development
`WinMux.exe` launched with an isolated directory opened and exited normally; its crash log carried
the informational version, while the installed App Paths entry still named the installed executable.

## What failed

Running a development executable through `dotnet WinMux.dll` avoided App Paths registration but
still shared all other user state. Merely moving settings and the session would leave launch
forwarding and CLI actions connected to the installed application. A separate directory therefore
has to define the endpoint identity as well as the files.
