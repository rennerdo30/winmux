# Changelog

Notable changes per release. Dates are absolute; the format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely and the versions are
[semantic](https://semver.org/), with the caveat that 0.x makes no compatibility promise.

Architectural reasoning lives in [`docs/adr/`](docs/adr/), not here. This file says what changed;
the ADRs say why.

## Unreleased

### Fixed

- A name given to an empty pane was lost as soon as something was opened in it. The pane that
  replaces it in place — a terminal, an application, an adopted window — now keeps the name.

## 0.7.2 — 2026-09-23

### Added

- **Drag and drop in the file browser**, between any two panes whatever their filesystems — local,
  network share, SFTP, FTP — and from Explorer into any pane. A local pane's files can be dragged out
  to Explorer. Drop on a folder to put things inside it; Ctrl copies, Shift moves
  ([ADR 0021](docs/adr/0021-file-browser-details-and-drag-and-drop.md)).
- **Multiple selection** with Ctrl and Shift click; cut, copy, paste, delete and drag act on all of it.
- **Real icons and details**: Windows' own file and folder icons, and Name / Date modified / Type /
  Size columns, sortable by clicking a heading. Remote files get the same icons and types.
- **Every file-browser pane says where it is**: This PC, Network share, or the server and account,
  with plain FTP marked as unencrypted.
- **Try again** on a server pane that could not connect, asking for the password again.

### Fixed

- A refused password showed as three run-on sentences with no way forward but closing the pane.
- The headless test app was never installed, so every headless test ran without control templates.

## 0.7.1 — 2026-09-23

### Added

- **Copy and move between filesystems** in the file browser: an SFTP or FTP server and the local
  disk, or two different servers. Copy in one pane, paste in the other; folders go whole, progress
  shows in the status line, nothing is overwritten, and an interrupted move leaves the original
  untouched ([ADR 0020 addendum](docs/adr/0020-sftp-and-ftp.md#addendum-2026-09-23--copying-between-filesystems)).

### Fixed

- Pasting something copied in a *different* file-browser pane on another filesystem handed its path
  to the wrong filesystem and failed.
- A pane you moved a file out of went on listing it until refreshed by hand.
- Clicking the empty part of a file-browser pane did not make it the focused pane, so the next
  `Ctrl+V` went to the other one. Moving to a file-browser pane from the keyboard had the same fault.
- A cancelled file operation did not say it had been cancelled.
- Closing a remote pane could block the window while that connection was busy.

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

- **Every line in a terminal pane could end up underlined**, including plain shell output. The VT
  engine read `ESC[>4m` — a *private* sequence that sets a keyboard protocol and means nothing
  about styling — as `ESC[4m`, underline on, with nothing ever to clear it. Programs that send it
  while starting up, Claude Code among them, left the whole pane and everything printed afterwards
  underlined. A second defect in the same area, `ESC[4:0m` (underline off) being read as underline
  on, is fixed too. WinMux repairs the byte stream before the engine sees it; the engine is
  third-party with no reachable repository, so there was nowhere to send the patch. See
  [ADR 0018](docs/adr/0018-terminal-emulation-supply-chain.md).
- **The release package could not start WinMux.** The CLI overwrote the shell's executable, so the
  file the README told people to double-click was the console CLI. Both the publish script's own
  file check and the updater's archive check asked whether `WinMux.exe` existed, and the CLI
  satisfied both — two verifications, one blind spot, because neither looked at *what* the file
  was. `publish.ps1` now checks the PE subsystem and that the two executables are not the same
  file. No release had been published, so this never reached anyone.
- **Switching tab left the keyboard behind.** Selecting another tab moved what was shown but not
  where typing went, so the first thing typed after a switch went to the tab you had left. Cycling
  with `Ctrl+B n` focused nothing at all; clicking a tab focused it before it had been laid out,
  which does nothing.
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
