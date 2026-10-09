# ADR 0030 — A per-user installer, and updating from About

- **Status:** accepted
- **Date:** 2026-10-09
- **Spike:** none; verified end to end (below)

## Context

Three things the owner asked for on 2026-10-09:

1. **CI should build an installer.** Releases shipped a zip only. Getting started said "there is no
   installer", and every user chose their own folder — sometimes one they could not write to.
2. **Does auto-update really work?** Reading the code found two faults that no test covered:
   - The running version came from the *assembly* version, which has no room for `-test.8`. A test
     build believed it was `0.7.8`, and since SemVer orders every `0.7.8-…` before `0.7.8`, no
     later test build was ever newer. The prerelease channel could not offer anything to a
     prerelease.
   - `UpdateService.CheckAsync` returned "up to date" whenever *automatic* checks were off, so
     **Help → Check for updates** told someone with checks disabled that they were current without
     asking anyone.
3. **The Help menu's "Check for updates" and "Install update…" were odd, and there was no About.**
   Every desktop application has an About box with the version and copyright, and browsers taught
   everyone that updating happens there: open it, it checks, it downloads, it offers a restart.

## Decision

**An Inno Setup installer, per-user only.** `installer/WinMux.iss` installs to
`%LOCALAPPDATA%\Programs\WinMux` with `PrivilegesRequired=lowest` and no override. The in-app
updater replaces WinMux's files in place (ADR 0023), so it needs a folder the user can write; a
Program Files install would install once and then fail every update. Per-user also means no UAC
prompt, which is right for an application that touches nothing outside the user's profile.

- **The zip stays the update package.** The updater still selects `-win-x64.zip`; the setup
  program is `-win-x64-setup.exe`, a name that cannot match it. Both are in `checksums.txt`.
- **Framework-dependent, like the zip.** Bundling the .NET runtime in the installer would help only
  until the first update replaced the files that used it. Setup checks for the .NET 10 Desktop
  Runtime and, if it is missing, offers the download page and continues.
- **Uninstall removes the whole folder**, because updates add files the installer never listed and
  leave `*.winmux-old` copies until the next start. Sessions and settings are in `%APPDATA%\WinMux`
  and are kept. The App Paths registration WinMux writes for itself is removed only if it points
  into the folder being uninstalled.
- **The installed version stays truthful.** After a successful in-app update, the swap script sets
  `DisplayVersion` under the installer's `AppId` — only if that entry's `InstallLocation` is the
  folder it just updated, so an unzipped copy elsewhere never claims to be the installed one.
  `UpdateInstaller.InstallerAppId` and the `.iss` carry the same GUID; never change it.
- **The compiler is pinned and verified.** `scripts/install-inno-setup.ps1` downloads Inno Setup
  **7.1.0 (released 2026-08-12, the newest on jrsoftware.org)**, checks a pinned SHA-256 and a valid
  Authenticode signature from Pyrsys B.V., and installs it portably. Same reasoning as the NuGet lock
  files (ADR 0018): the compiler is part of what produces shipped bytes.
- **CI installs and uninstalls it.** `scripts/test-installer.ps1` runs the real setup silently into
  a scratch folder, checks the files, the PE subsystem of `WinMux.exe`, the Windows listing and that
  the folder is writable, then uninstalls and checks that the folder — including a file standing in
  for one an update added — and the listing are gone. Both `ci.yml` and `release.yml` run it.

**Updating moves into an About window.** `AboutWindow` shows the mark, the full version, the
copyright, the MIT licence and the third-party notices (in a window, read from beside the
executable), links to the docs, releases, source and issues — and an update card driven by
`UpdateController`, a small state machine (idle → checking → up to date | available → downloading
→ ready to restart, or failed with retry) shared by everything that touches updates, so a download
started anywhere is shown everywhere and survives the window being closed.

- **Opening About checks**, unless automatic checks are off: that setting promises no network
  access, so the window says checks are off and offers a button instead.
- **Downloading is a click; restarting is another.** Download is harmless; restart closes every
  program in a pane, so it waits for "Restart WinMux", beneath a line saying exactly that.
- **The Help menu is Documentation, Release notes, Report an issue, About WinMux.** The two update
  items are gone. `check-for-updates` and `install-update` remain named actions for the palette and
  CLI (`wmux update`, `wmux about`); both open About, which is now their visible control.
- **The startup offer leads to About.** Accepting it opens About and starts the download there,
  where the progress and the restart button are.
- **Manual checks always ask GitHub.** Whether to check on start is the caller's decision now.
- **The running version is the informational version**, which keeps the suffix (and build metadata,
  which ordering ignores).
- **A folder that cannot be written is refused before downloading**, with a sentence pointing at the
  installer, instead of a rollback after a restart.

## Verified

On 2026-10-09, on this machine: a local build numbered 0.7.3 was packaged by the new installer,
installed silently into a scratch folder, started, and asked to update with `wmux action
install-update`. It found v0.7.7 on GitHub's stable channel, downloaded and verified it, and showed
"Restart WinMux"; invoking that button through UI Automation closed it, the swap script replaced 77
files and added 4, recorded 0.7.7 as the installed version, and started 0.7.7 from the same folder.
The installer then uninstalled cleanly. `scripts/test-installer.ps1` passes on the real setup.

## Consequences

- A release now has three assets: the zip (updates and portable use), the setup program, and
  `checksums.txt` covering both.
- The setup program is not code-signed, so SmartScreen will warn on first run. Signing needs a
  certificate, which is the owner's call.
- Anyone with WinMux unzipped under Program Files is told why it cannot update and where the
  installer is, rather than discovering it through a failed restart.
