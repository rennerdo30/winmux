# Changelog

Notable changes per release. Dates are absolute; the format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely and the versions are
[semantic](https://semver.org/), with the caveat that 0.x makes no compatibility promise.

Architectural reasoning lives in [`docs/adr/`](docs/adr/), not here. This file says what changed;
the ADRs say why.

## Unreleased

### Added

- **An empty pane offers to become a tab group.** Making a pane a tab group is a decision about
  the shape around it rather than about what goes in it, and it was reachable only from the toolbar
  — a long way from the pane that is asking the question.
- **A pane asking for you marks its tab.** When a program raises a notification — Claude Code
  waiting on a permission, a bell — an accent dot appears on that pane's tab, with what it said on
  the tooltip, and stays until you look at the pane. The notification tells you *that* something
  happened; the dot tells you *which* of six panes, which is the part that was missing. It follows
  the same Notifications setting, and unlike the toast it is not rate-limited: a program still
  ringing is a program that still wants you.
- **A tab drag shows where the tab will land.** An accent caret in the gap the drop will fill — on
  the gap rather than on a tab, because "between which two" is the question a drag asks and
  highlighting one tab cannot say before or after. The strip about to receive it takes the accent
  on its edge, since a two-pixel line is easy to miss with four strips on screen, and the tab being
  carried fades where it sits.

## 0.7.7 — 2026-09-24

### Added

- **A tab can be dragged from one tab group to another**, not only along its own strip. The pane
  travels with its runtime still running — a terminal keeps its shell and its scrollback — because
  the same pane is carried across rather than rebuilt. Dropping on the empty part of a strip puts
  the tab at the end, and a group left holding one tab collapses.

### Fixed

- **"Tab group" added another tab to the group the pane was already in**, instead of making a
  group. The toolbar button has always said "turn the focused pane into a tab group" and did
  exactly that once — on every press after the first it added a tab to the group it had made.
  A pane already in a group now gets a group nested inside it. **"New tab, tabs down the side" was
  worse**: it found the *enclosing* group and moved its strip, so asking for a side-tabbed group
  from inside a top-tabbed one moved every existing tab to the side instead of making anything.
- **Selecting a tab often needed a second click while a full-screen program was running.** A
  terminal pane posts a repaint whenever its engine updates, at a higher dispatcher priority than
  the job that moved the keyboard to the newly selected pane — and Claude Code, vim or a build
  posts them faster than they drain, so that job waited for a gap that never came. The tab switched
  and the keyboard did not. Focus no longer waits in a queue: the layout it was waiting for is run
  directly. The same starvation could hold up the file browser restoring focus to its list, because
  every pane in a window shares one dispatcher.
- **A tab group's `+` added its tab to the group inside it** when that group's active tab was
  itself a tab group or a split. It asked for a tab *beside a pane*, which is the same thing only
  while the active tab is a single pane.

## 0.7.6 — 2026-09-24

### Added

- **URLs in a terminal pane open on `Ctrl`+click.** Hold `Ctrl` and the link under the pointer is
  underlined with a hand cursor. Both kinds work — a link a program marked up with OSC 8, which
  the engine had been parsing and the renderer had been discarding, and a plain `http://` or
  `https://` in ordinary output. Only `http` and `https` ever open: a pane shows untrusted program
  output, and a `file:` URL or a custom scheme would make a click on a word a way to run something.
- **Tabs can be pinned**, so that closing a pane leaves them alone. A pinned tab shows a pin where
  its close button was — clicking it unpins — and `close-pane` refuses with a reason rather than
  silently doing nothing. `Ctrl+B .`, or the tab's right-click menu, or `toggle-pin` from the
  palette and the CLI. The pin is saved with the session.

### Changed

- **The settings dialog got a pass over what it actually looks like**, rendered rather than
  reasoned about. The three file paths wrapped into right-aligned fragments — `C:` alone on the
  first line, `ml` alone on the third — and are now one line that ellipsises by whole directories,
  with the full path on the tooltip and an **Open folder** button beside it. The window is
  **resizable** and opens as tall as the screen allows, instead of being fixed at a 640px viewport
  over 2,100px of content. The profile list no longer cuts its last row through the middle.
  **Remove** is separated from the four buttons it sat flush against and reddens under the pointer.
- **A notification block is its own warning, with the button that fixes it.** "Windows
  notifications are turned off" was appended to a description in the same muted grey as everything
  else, so the one thing on the page that needed doing was the easiest to miss.

### Documentation

- [Keyboard](https://winmux.dev/keyboard/) covers `Ctrl`+click on a link and the `.` pin key.

## 0.7.5 — 2026-09-24

*Tagged after 0.7.6, which contains it. Use 0.7.6 unless you want this fix on its own.*

### Fixed

- **WinMux crashed when a full-screen program started in a pane that held scrollback.** Starting
  vim, less, htop or Claude Code discards the whole scrollback in one write, and the repaint already
  in flight was still copying rows by their old indices — `IndexOutOfRangeException`, from the render
  pass, taking the process with it. Reading a row is now total: a row that is no longer there reads
  as empty and the next repaint is correct
  ([ADR 0024](docs/adr/0024-reading-a-terminal-while-it-is-written-to.md)). The same fault could be
  reached from copy, select-word and search, not only from drawing.
- **No cursor in a full-screen program started after scrolling back.** The view stayed parked above
  history that no longer existed, and the cursor is deliberately not drawn while you are reading
  history.

### Documentation

- [Troubleshooting](https://winmux.dev/troubleshooting/) says where the crash log is
  (`%LOCALAPPDATA%\WinMux\crash.log`), that it is capped at 1 MB and deleted rather than rotated,
  and what an empty one means.

## 0.7.4 — 2026-09-24

### Fixed

- **Claude Code was unusable in a terminal pane**: its trust dialog could not be answered and its
  screens were drawn over each other. At startup it asks `ESC[?u` (kitty keyboard protocol), which
  the terminal engine executed as `ESC[u`, restore cursor, sending every later cursor move to the
  wrong row. Private keyboard-protocol and version queries are now filtered before the engine
  ([ADR 0018 addendum](docs/adr/0018-terminal-emulation-supply-chain.md)).
- **`Alt+V` image paste in Claude Code** sent Alt+Shift+V: Windows reports the key's character as
  "V" while Alt is held. An Alt+letter now takes its case from Shift.

- **The updater could not install**, and did not restart WinMux when it failed
  ([ADR 0023](docs/adr/0023-session-location-and-in-place-updates.md)). It renamed the whole
  installation directory, which Windows refuses while anything has a handle inside it. It now
  replaces files one at a time — renaming each old one aside, which Windows allows even for a file
  in use — rolls everything back if any file cannot be replaced, **always starts WinMux again**, and
  reports the outcome in the status bar with a log in `%LOCALAPPDATA%\WinMux\update.log`.
  **Updating from 0.7.3 or earlier needs one manual install**: the old version's installer is the
  one that runs.
- **The session could be deleted by an update.** Its default was `session.toml` in the working
  directory — the installation, when WinMux was started by double-clicking it — and the old updater
  deleted the old installation. The session now lives in `%APPDATA%\WinMux\session.toml`; one left in
  the old place is copied there on first start and the original kept.

### Added

- **Diagnostics for terminal problems**, off unless asked for: `WINMUX_DEBUG_KEYS=1` logs what
  each key became (`%LOCALAPPDATA%\WinMux\keys-debug.log` — it records what is typed), and
  `WINMUX_DEBUG_PTY=1` captures a pane's raw output for replay.

## 0.7.3 — 2026-09-23

### Added

- **Windows notifications from terminal panes** ([ADR 0022](docs/adr/0022-terminal-notifications.md)).
  When a program asks for attention — OSC 9, 777 or 99, or a bell — in a pane you are not looking at,
  WinMux shows a notification naming the pane; clicking it brings you to that pane. A setting chooses
  messages and bells, messages only, or off.
- **Claude Code setup**: one button in Settings points Claude Code's notifications at WinMux, which
  it does not otherwise recognise.
- **Focus reporting** (`ESC [ I` / `ESC [ O`) for programs that ask for it.
- **A crash log** at `%LOCALAPPDATA%\WinMux\crash.log`.

### Fixed

- **Copy and paste in terminal panes**, found with Claude Code: `Ctrl+C` with text selected sent an
  interrupt instead of copying; `Ctrl+V` did not paste; `Alt` combinations sent nothing, so
  Claude Code's `Alt+V` image paste could not work; `Shift+Tab` sent a plain Tab; pastes were never
  bracketed, so a multi-line paste ran line by line; and programs could not copy to the clipboard
  (OSC 52). All six are fixed. Function keys and modified arrows now reach programs too.
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
