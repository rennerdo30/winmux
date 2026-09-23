# ADR 0023 — Session location, and updates that replace files in place

- **Status:** accepted
- **Date:** 2026-09-24
- **Spike:** none

## Context

Reported on 2026-09-24: the updater "correctly detects the new version and downloads it, but install
and restart fails". Reading the code for why turned up something worse.

- **The session file defaulted to `session.toml` relative to the working directory.** Double-click
  `WinMux.exe` and the working directory is the installation, so the session was saved inside it.
  Settings and profiles were already absolute, in `%APPDATA%\WinMux`; the session was the exception
  — and the one thing priority 1 says must survive.
- **The updater renamed the whole installation directory** to `*.previous`, moved the staged
  version into place, started it, and deleted `*.previous` three seconds later — taking a session
  kept there with it. A directory cannot be renamed while anything holds a handle inside it: the
  bundled `OpenConsole.exe` of a terminal still shutting down, a pane host, a shell whose working
  directory it is, an Explorer window showing it. The rename threw, the script put things back and
  re-threw — in a hidden PowerShell window, with no log — and nothing started WinMux again.
- It staged beside the installation, whose parent directory need not be writable, and checked only
  that the archive contained *a file called* `WinMux.exe` (CLAUDE.md section 3 says why that is not
  enough).

## Decision

- **The default session lives at `%APPDATA%\WinMux\session.toml`** (`SessionLocation`). An explicit
  path still wins. A `session.toml` found in the working directory or the installation directory is
  *copied* to the new home the first time — never moved or deleted — and the status bar says where
  it now is.
- **An update replaces files, not the directory.** The script waits for WinMux to exit (up to five
  minutes; if it never does, nothing is changed), backs up `foreign-app-quirks.json`, then for each
  file renames the old one to `*.winmux-old` — Windows permits renaming a file in use; it refuses
  to overwrite or delete one — and copies the new one in, retrying briefly while a process lets go.
  Any failure rolls back every file already replaced or added.
- **WinMux always starts again** — the new version or the old one restored — from its own directory,
  on the session it was using. The outcome goes to `%LOCALAPPDATA%\WinMux\update-result.txt`, which
  the next start reports in the status bar; every step goes to `update.log`. The `*.winmux-old`
  files are deleted on that next start, when nothing runs them.
- Staging moves to `%LOCALAPPDATA%\WinMux\update-staged`. The staged `WinMux.exe` must be a windowed
  PE image (subsystem 2), not merely present.
- Closing for an update that cannot finish (a pane that will not close safely) says the update is
  waiting, instead of only that WinMux stayed open.

## Consequences

- An update can no longer delete a session, and a failed one leaves the previous version running.
- **Versions up to 0.7.3 still carry the old script**, and the running version's script is the one
  that installs. Moving to 0.7.4 needs one manual unpack; after that the new installer applies.
- The installation directory can contain `*.winmux-old` files between an update and the next start.
- The script is tested end to end: `UpdateScriptTests` runs it with PowerShell against a pretend
  installation, with a file held open the way a running image is, and with a file locked outright.
  Both guards were verified by breaking them — a plain overwrite fails on the file in use, which is
  very likely what the user met.

## What failed

- The first updater was verified by building a package and starting it from the zip, which never
  exercised the swap against a running installation — the only situation it exists for.
- It reported failure nowhere. A hidden script that throws is indistinguishable from one that was
  never run; the log and the result file exist so that "it fails" comes with a reason.
