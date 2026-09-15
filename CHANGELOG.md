# Changelog

Notable changes per release. Dates are absolute; the format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely and the versions are
[semantic](https://semver.org/), with the caveat that 0.x makes no compatibility promise.

Architectural reasoning lives in [`docs/adr/`](docs/adr/), not here. This file says what changed;
the ADRs say why.

## 0.7.0 — 2026-09-16

The first published release. WinMux has been usable for a while; this is the point at which the
package that comes out of `publish.ps1` is one worth handing to somebody.

### Added

- **File operations in the file browser.** New folder, rename, cut, copy, paste and delete, each
  with a key, a context-menu entry and — for the common ones — a toolbar button. Nothing
  overwrites: a name collision becomes `report (2).txt` whether it arrives by paste or by rename.
  Delete goes to the Recycle Bin, and a *failed* recycle is never quietly turned into a permanent
  delete.
- **SFTP and FTP connections** ([ADR 0020](docs/adr/0020-sftp-and-ftp.md)). A saved connection is a
  profile like any other and opens a file-browser pane on the remote machine, with the same
  operations and the same rules. SFTP can authenticate with a key. FTP upgrades to FTPS wherever
  the server offers it, and says plainly when it cannot.
- **Saved SSH and Remote Desktop connections**, which delegate to the Windows clients rather than
  reimplementing them.
- **A credential store.** Passwords live in Windows Credential Manager, never in a file WinMux
  owns. The profiles file and the session file are plain text by design, and a test asserts that
  no key written to either is named after a secret.
- **A documentation site** at <https://rennerdo30.github.io/winmux/>, including a guide to reaching
  [network shares](docs/src/content/docs/network-shares.mdx) through Windows' own SMB and NFS
  support.
- **An in-app updater** that checks GitHub, installs nothing without being asked, and refuses any
  archive whose SHA-256 is not in the release's `checksums.txt`.
- **Continuous integration**: build, test and package on Windows with `--locked-mode` and warnings
  as errors.
- **`THIRD-PARTY-NOTICES.txt`** in the package, generated from what is actually shipped rather than
  maintained by hand.

### Changed

- **The command line is now `wmux.exe`, not `winmux.exe`.** It had to move: Windows filenames are
  case-insensitive, so `winmux.exe` and the shell's `WinMux.exe` were one file in the package
  directory, and the CLI — published second — overwrote the shell. See *Fixed*.
- The terminal measures its own font instead of assuming a fixed cell size, and the family and size
  are settings.

### Fixed

- **The release package could not start WinMux.** The CLI overwrote the shell's executable, so the
  file the README told people to double-click was the console CLI. Both the publish script's own
  file check and the updater's archive check asked whether `WinMux.exe` existed, and the CLI
  satisfied both — two verifications, one blind spot, because neither looked at *what* the file
  was. `publish.ps1` now checks the PE subsystem and that the two executables are not the same
  file. No release had been published, so this never reached anyone.
- **Typing a path into the file browser's address bar did nothing.** It read the text box from a
  background thread, which Avalonia refuses. Broken since the file browser shipped.
- **Copy-then-paste from the keyboard did nothing.** Rebuilding the list destroyed the focused row,
  so the `Ctrl+V` after a `Ctrl+C` went nowhere.
- **Editing a saved connection's host silently discarded the change.** `LaunchProfile`'s
  hand-written equality omitted the connection fields, so the settings UI concluded nothing had
  changed.
- Symbol key bindings on non-US keyboards, which had never worked on a layout where `:` is not
  `Shift+;`.
- A terminal rendering bug that drifted every coloured run, cursor and selection away from its
  glyphs, because the cell width was a constant rather than a measurement.

### Known limitations

- **Mixed-scale multi-monitor is unverified.** `scripts/verify-mixed-dpi.ps1` exists and has never
  been run on hardware with two different scale factors.
- **SFTP and FTP are verified against SFTPGo on loopback**, not against the variety of real servers
  out there. `WINMUX_TEST_SFTP` points the live tests at any host.
- **`Terminal.Emulation`**, the VT engine, is a single-author package whose declared source
  repository does not resolve ([ADR 0018](docs/adr/0018-terminal-emulation-supply-chain.md)). It is
  MIT and hash-pinned, and the v1 decision is still open.
- Restoring a session recreates processes from their descriptors. It does not restore what was
  running inside them.
