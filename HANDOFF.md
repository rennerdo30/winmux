# HANDOFF — WinMux

## Where we are

Phases 0–5 complete; Phase 6 implemented with native acceptance still open. Source version:
**`0.7.8`, the first stable release since 0.7.7**, [published 2026-10-09](https://github.com/rennerdo30/winmux/releases/tag/v0.7.8)
as GitHub's latest release with the zip, the setup program and checksums.txt (both hashes verified
after download): the 0.7.8 test builds plus a
per-user **setup program built by CI** and a **Help → About WinMux** window that checks, downloads
and installs updates ([ADR 0030](docs/adr/0030-installer-and-about-window.md)).
Explorer launches route into one process per user ([ADR 0029](docs/adr/0029-explorer-single-instance-launch.md)).

## What just happened

**2026-10-09:** `installer/WinMux.iss` (Inno Setup 7.1.0, pinned by hash and signature in
`scripts/install-inno-setup.ps1`) installs to `%LOCALAPPDATA%\Programs\WinMux` without admin.
`scripts/build-installer.ps1` builds it; `scripts/test-installer.ps1` installs, checks and
uninstalls it; both `ci.yml` and `release.yml` run them, and the release uploads the setup program
with it in `checksums.txt`. The Help menu's two update items became **About WinMux**
(`AboutWindow`, `UpdateController`, new action `show-about`, CLI `wmux about` / `wmux update`).
Fixed: the running version dropped `-test.N` (prereleases were never offered a newer prerelease);
a manual check with automatic checks off answered "up to date" without asking.
**Verified end to end:** a local 0.7.3 build, installed by the new setup program, updated itself
from GitHub to v0.7.7 (77 replaced, 4 added, Installed-apps version updated, relaunched); test
install uninstalled and the owner's App Paths entry restored. Release build 0 warnings; full suite
**1,277 passed, 6 live FTP/SFTP skips**. Main CI (run 37895299051) passed including the new
Installer and Test installer steps. The docs deploy was killed for memory on its runner (local
build passes) and was re-run.
SDK: `%LOCALAPPDATA%\WinMuxDev\dotnet\dotnet.exe`.
Standing constraints: [development guardrails](docs/development-guardrails.md).

## The next action

Install v0.7.8 from its setup program on a clean machine (or after uninstalling an unzipped copy)
and check About's version, licence and notices by hand; let About install 0.7.9 when it ships.

## Blocked / needs a human

The setup program is unsigned, so SmartScreen warns; signing needs a certificate (owner's call).
Native Explorer/foreground activation, elevated desktop boundaries, drag/DPI feel, GPU/fallback/VM
and mixed-DPI acceptance remain open. Stable release remains open. Connection-source follow-ups:
RDCMan file selection/WinSCP registry editing ([ADR 0025](docs/adr/0025-foreign-connection-sources.md)).

## Do not re-do

Never offer a machine-wide install: the updater must write its own folder. Never change the
installer `AppId` (shared with `UpdateInstaller.InstallerAppId`). Keep the zip as the update
package. Read the version from the informational version, never `AssemblyName.Version`. Keep
default sessions in AppData. No real registry writes in unit tests (the script test uses a scratch
key it deletes). Retain software fallback and unchanged lockfiles.
