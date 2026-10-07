# ADR 0027 — Terminal and tab interaction

- **Status:** accepted; native application acceptance open
- **Date:** 2026-10-07
- **Spike:** none

## Context

The user reported accidental unpin/close, uneven title-bar separators, missing Claude Code
wheel input and Ctrl+B, swallowed tab clicks during output, and nested tab selection resets.
They also requested confirmation before closing terminals with running programs.

## Decision

Use Ctrl+Shift+P as the default command prefix, preserving custom keymaps. Repeating the
configured prefix passes it to the terminal; the send-prefix action uses that configured gesture.
Route wheel input according to the terminal engine's mouse and alternate-screen modes, including
SGR and legacy reports, alternate-scroll cursor sequences, fractional deltas, and Shift-wheel
history override. Terminal query replies must not reset history or selection.

Coalesce repaint, title and runtime state work at Background priority, below Input. Retain tab
buttons and open menus across metadata/focus refreshes. Ignore late focus events from hidden
panes. The existing Core active-leaf memory remains authoritative for nested tab selection.

Clicking a pin opens an explicit Unpin menu; pinned tabs disable their close menu item. TitleBar
owns the single full-width bottom separator; ShellToolbar must not draw another one.

Inspect fresh process trees off the UI thread when closing terminals or the window, honoring
ConfirmBeforeClosingPanes. Protect child programs, directly launched applications and WSL;
inspection failure asks conservatively. Idle CMD/PowerShell and non-terminal panes need no
new confirmation. Known console infrastructure is ignored. Shell builtins with no child process
cannot be distinguished from an idle shell by this check. Existing session replacement and
update confirmation remain their own explicit gates.

## Validation and limits

Tests cover mode parsing, wire encoding, default/custom prefix routing, repeated pin clicks,
retained controls and menus, nested tab activation, process graphs and inspection failures.
Real-control tests inject a recording PTY and exercise wheel events, cursor queries and flood-time
Ctrl+B. Final solution Release build has zero warnings/errors; 1,198 tests pass, six live FTP/SFTP
tests skip, and none fail.
The dispatcher regression checks that input runs before repeated repaint work drains; restoring
Render priority fails with 200 paints before input, versus at most one with Background.

Skia headless captures and pixel samples at 100% and 150% verify a uniform separator across
the title bar. Restoring the old inner toolbar border fails both separator regressions.
Local captures are in ignored artifacts/review/chrome-*.png and chrome-verification.json.
Native Windows frame/corner appearance, sustained-output feel, Claude Code controls and GPU/VM
behavior still need interactive acceptance; automated tests do not establish those results.

## What failed

Coalescing at Render priority bounded the queue but still let a self-replenishing repaint starve
input. The old test asserted only that input eventually ran after a finite burst, so it missed
the problem. Rebuilding tab controls on every title change also interrupted press/release and
open menus. Ctrl+B was consumed by the shell before terminal encoding. Always scrolling local
history ignored applications requesting mouse reporting. Query replies reused the user-input
path, which snapped a scrolled view to the bottom. The toolbar and title bar both drew a separator.

The running user's executable locked the ordinary build output. Validation builds use the
separate artifacts/review directory; the user's running sessions were left intact.
