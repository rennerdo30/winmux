# HANDOFF — WinMux

## Where we are

Phases 0–5 complete; Phase 6 implemented with native acceptance still open. Source version:
`0.7.8-test.7`. Both test.6 and test.7 were published successfully; test.7 includes all current fixes.
See ADRs [0026](docs/adr/0026-optional-gpu-rendering.md),
[0027](docs/adr/0027-terminal-and-tab-interaction.md) and
[0028](docs/adr/0028-tab-width-and-terminal-key-ownership.md).

## What just happened

**2026-10-07:** Optional GPU/software preference, application wheel input, Ctrl+Shift+P prefix,
responsive retained tabs, safer pin/unpin, uniform separator and busy-terminal close confirmation
were committed and released as test.6; GitHub build/tests/package/publish succeeded.
Follow-up adds Saved connections search (Ctrl+F); compact vertical rows and grouped Add/More;
independent per-group strip-width dragging persisted as optional tab_width; and preserved explicit
parent labels when wrapping tabs. Cancelled rename keeps the previous label.
Ctrl+C always interrupts; Ctrl+V/Escape reach the application even with selection/clipboard text.
Copy: Ctrl+Shift+C or Ctrl+Insert. Paste: Ctrl+Shift+V or Shift+Insert. Alt+V preserved.
Release solution build: 0 warnings/errors. Full suite: 1,236 passed, six live FTP/SFTP skips,
zero failures. Real headless pointer drag and fake-PTY control tests cover resize/key wiring;
Skia 150% captures show compact rows and search. Native acceptance remains open.
Pinned SDK: `%LOCALAPPDATA%\WinMuxDev\dotnet\dotnet.exe`. Builds use ignored artifacts/review
because the user's ordinary executable is running. Test.7 local package passed required-file,
GUI/CLI subsystem, component-version and packaged CLI validation. GitHub release build, tests,
package and publication succeeded; downloaded archive matches published SHA-256 checksums.
Release: https://github.com/rennerdo30/winmux/releases/tag/v0.7.8-test.7
Standing constraints: [development guardrails](docs/development-guardrails.md).

## The next action

Run test.7 and check strip dragging/save/restart, nested rename, search, Ctrl+C while selected,
Claude wheel/Alt+V, tab switching and menus during output, pin/unpin and busy/idle close prompts.

## Blocked / needs a human

Native Windows drag/DPI feel, live Claude Code, GPU/fallback/VM execution, frame/corners and physical
mixed-DPI acceptance remain unmeasured. GPU-first was already Avalonia's default; no speedup claim.
Process detection cannot identify shell builtins with no child; WSL protected conservatively.
Stable release remains open. Connection-source follow-ups: RDCMan file selection and WinSCP registry
editing ([ADR 0025](docs/adr/0025-foreign-connection-sources.md)).

## Do not re-do

Read guardrails and CLAUDE.md before embedding/persistence/focus changes. No foreign HWND calls on
UI thread, hard-killing hosts owning children or claims of native acceptance from headless tests.
Keep software fallback/settings-before-App.Initialize. Repaint/title/state work yields to Input;
retain tab controls for metadata/resize and Core active-leaf memory. Keep lockfiles unchanged when
packaging and verify GUI/CLI PE subsystems. Do not overwrite existing published tags.
