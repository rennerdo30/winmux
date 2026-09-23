# ADR 0022 — Terminal notifications

- **Status:** accepted
- **Date:** 2026-09-23
- **Spike:** none

## Context

Asked on 2026-09-23: can WinMux pick up the alerts a program like Claude Code sends from a terminal,
and show them as Windows notifications?

Programs ask for attention in four ways, and none of them reached the user in WinMux: OSC 9
(iTerm2's message), OSC 777 `notify;title;body` (urxvt, Ghostty), OSC 99 (kitty) and a bare BEL.
WinMux parsed OSC only for working directories.

Two facts about Claude Code, from its documentation and a closed feature request, shaped the design.
It "sends a desktop notification only in Ghostty, Kitty, and iTerm2"; anywhere else it sends
nothing unless `preferredNotifChannel` in its `settings.json` says otherwise (`iterm2`,
`iterm2_with_bell`, `kitty`, `ghostty`, `terminal_bell`, `notifications_disabled`; a `windows_toast`
value was requested and declined). And it notifies only when "you appear to be away" — which a
terminal tells a program with focus reports, `ESC [ I` and `ESC [ O` after DECSET 1004. WinMux did
not send those at all.

## Decision

- **Parse all four.** `OscObserver` (renamed from `OscWorkingDirectoryParser`, since it now does more)
  reports a `TerminalNotification` alongside working directories. OSC 9 is shared with ConEmu, whose
  numbered subcommands — `9;9` the working directory, `9;4` progress — are not messages. A BEL that
  terminates an OSC is not a bell. Text is stripped of control characters and capped at 500
  characters, because it comes from whatever is running and is about to be shown outside it.
- **Report focus.** A terminal pane sends `ESC [ I`/`ESC [ O` when a program has asked for them, on
  transitions only. Focused means the pane has the keyboard *and* WinMux is the active window —
  switching application is looking away as much as switching pane.
- **Notify only when it helps.** `TerminalAttention` decides: never for the pane being looked at,
  at most one per pane every 3 seconds, the same words not again for 30, and a bare bell only when
  the setting says bells count. The heading names the pane, because "Claude needs your permission"
  means nothing until you know which of several sessions said it. The status bar says it too.
- **Show it through the notification area.** `IDesktopNotifier` (`WinMux.Platform`) is implemented
  in `WinMux.Platform.Win32` with `Shell_NotifyIcon` and a balloon, which Windows 10 and 11 show as
  an ordinary toast, keep in the notification centre and hold back during Focus Assist. The WinRT
  toast API would need an SDK-versioned target framework on the platform projects, a registered
  AppUserModelID and a COM activator before a click could be delivered; the balloon needs none of it
  and reports a click as a window message. It runs on its own thread with a hidden window, so it
  never touches the UI thread. Its icon in the notification area is removed when the user comes back
  to WinMux, and on exit.
- **Clicking goes back.** WinMux comes forward with the pane that asked in focus, its tab revealed.
- **Say when Windows will not show it.** With notifications switched off in Settings › System ›
  Notifications, Windows shows no toast from anyone and tells the sender nothing. `BlockedReason`
  reads that switch, and the status bar and Settings say so.
- **One setting, one button.** *Notifications*: messages and bells (default), messages only, off.
  *Claude Code notifications*: sets `preferredNotifChannel` to `iterm2` (OSC 9, which carries the
  message where a bell carries none) in Claude Code's `settings.json` — respecting
  `CLAUDE_CONFIG_DIR` — after saying exactly what will change and where. Every other key is kept, a
  backup is written first, and a file that is not plain JSON (comments, trailing commas) is refused
  rather than rewritten without them.

## Consequences

- Any program that uses these conventions notifies through WinMux, not only Claude Code.
- `WinMux.Platform` gains `IDesktopNotifier`; the shell still declares no `DllImport`.
- WinMux now writes to another program's configuration, once, on request, with a backup.
- Focus reports reach every program that asks, so editors that reload on focus get that too.
- A crash log (`%LOCALAPPDATA%\WinMux\crash.log`) records unhandled exceptions from any thread and
  one line per exit and window close. WinMux had no record of how it ended before.

## What failed

- **The notifications were sent and never seen**, because Windows notifications were switched off on
  the development machine (`PushNotifications\ToastEnabled = 0`). Found by asking the system
  (`SHQueryUserNotificationState` said notifications were accepted — it does not reflect that
  switch) and the registry, after confirming the notifier's own calls succeeded. Hence
  `BlockedReason`. **The toast itself has not been seen on screen**; that needs the switch on.
- **"WinMux exits on its own after Claude Code runs"** was the user closing test windows the verifier
  had left open. Two clean exits with code 0 were taken for a bug, and a crash log was built to hunt
  it. The log is worth keeping; the lesson is to close every instance a check starts.
- **Synthetic keystrokes went to the wrong window.** `SendKeys` after `AppActivate`, without
  confirming the foreground window, typed a command and an Enter into another application at least
  once. See HANDOFF, *Driving the UI from a script*: check the foreground window's handle before
  every keystroke, or do not send it.
- `SendKeys` treats `+ ^ % ~ ( ) { }` as control characters, which mangled a PowerShell one-liner
  into a parse error that looked like a WinMux fault. Commands for a test go in a script file.
- Claude Code in a WinMux pane started from the verifier's own Claude session inherited its
  environment (`CLAUDE_CODE_CHILD_SESSION`), which a user's would not. A clean test launches WinMux
  outside it.
