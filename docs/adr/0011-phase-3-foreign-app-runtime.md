# ADR 0011 — Phase 3 foreign-app runtime and compatibility selection

- **Status:** accepted
- **Date:** 2026-09-12
- **Code:** `WinMux.Platform.Win32`, `WinMux.PaneHost`, `WinMux.Shell/ForeignAppPane.cs`
- **Acceptance:** `scripts/phase3-demo.ps1`, `WinMux.PaneHost.Tests/verify-attach.ps1`

## Context

Phase 1 established the non-negotiable process boundary: one top-level PaneHost per foreign pane,
with the shell positioning only that host HWND. Phase 0 measured which applications accepted
`SetParent`, seeded compatibility data, and found distinct refusals for higher-integrity Task
Manager (error 5) and packaged Calculator (error 87). The product still ignored the persisted
strategy and quirks seed, implemented embed only, and stopped at either refusal.

Phase 3 had to make those measurements control the product without moving a synchronous foreign
HWND call onto Avalonia's UI thread or risking an invisible orphan during fallback and shutdown.

## Decision

1. Foreign descriptors accept `auto`, `embed`, and `attach`. Missing foreign-app strategy means
   `auto`; missing strategy on other pane kinds retains the old embed-shaped default. Auto uses a
   measured rule and falls back to explicitly unverified embed for an unknown application.

2. `WinMux.Platform.Win32` owns the versioned JSON model, strict validation, deterministic
   specificity matching, and defaults. The measured seed ships beside `WinMux.exe` as
   `foreign-app-quirks.json`, intentionally editable by the user. Matching supports executable
   image, a distinct shim/launcher executable, window class, and optional title substring; selection also carries strategy, discovery
   mode, splash-settle delay, limitations, DPI evidence, and verification metadata.

3. The shell resolves a launch plan but makes no call against the application HWND. It passes the
   concrete strategy, class/title filters, settle delay, shell owner, and already-claimed HWNDs to
   PaneHost. Malformed or future quirks data blocks the affected pane with a visible path and error
   instead of silently reverting to guesses.

4. PaneHost implements both strategies. Embed adds `WS_CHILD`, clears `WS_POPUP` and frame styles,
   captures `SetParent` error immediately, and verifies with `GetAncestor(GA_PARENT)`. Attach leaves
   the application top-level and follows the host with `SWP_ASYNCWINDOWPOS`. PaneHost is PerMonitorV2
   and enables mixed DPI hosting on its window thread.

5. Any embed refusal restores the original parent/styles/rectangle before attaching the **same
   HWND**. It emits a user-readable, machine-detectable notice and the effective strategy. Errors 5,
   87, and parent-verification failure retain their specific explanations; unknown errors retain
   their Win32 code. No duplicate application is launched for fallback.

6. `STRATEGY=EMBED|ATTACH` changes a live pane in PaneHost. The command palette, CLI action surface,
   and prefix `A` expose `toggle-foreign-host-strategy`; the effective result is persisted. If an
   embed switch is refused, the application remains attached and usable.

7. Redirected-stdin EOF is treated as shell loss and triggers detach. `WM_DESTROY` also attempts
   restoration. Graceful detach/close is acknowledged only after parent/style restoration is
   verified; the shell cancels its close and stays visible if any host cannot confirm. Hard-killing
   a host that still owns an embedded child remains forbidden.

## Verification

On 2026-09-12, Release build completed with zero warnings. The final solution test gate contained
239 passing tests across Core, Shell, PaneHost, Win32 quirks, PTY, terminal, and CLI projects.

The desktop verifier measured:

- Character Map requested embed, became a child of PaneHost, followed a 720×680 host rectangle,
  followed hide/show visibility, switched **embed → attach → embed using the same HWND**, and
  survived verified detach as a top-level app.
- Calculator was launched through `calc.exe` and selected by `ApplicationFrameWindow`. Requested
  embed returned `ERROR_INVALID_PARAMETER` (87), automatically became attach with the visible
  `fallback=attach` reason, followed the host rectangle, and survived detach as a top-level app.
- `scripts/phase3-demo.ps1 -VerifyOnly` reproduces both checks. Its normal mode validates and opens
  a three-pane session containing a terminal, auto-embedded Character Map, and auto-attached
  Calculator for interactive resize, focus, live-switch, close, and detach inspection.

The broader Phase 0 compatibility rows for Notepad, Explorer, VS Code, synthetic DPI-unaware and
system-aware apps were not rerun through the final shell in this session; they remain evidence for
the shipped seed, not new Phase 3 measurements.

## Consequences

- Packaged apps can remain useful panes without pretending they were embedded.
- Per-app behavior is data rather than a growing chain of executable-name conditionals.
- Attach is a compatibility mode, not a containment mode: its window can retain frame/minimum-size
  behavior and is not clipped like an embedded child.
- Live strategy changes and fallback stay inside the disposable host process. The shell's UI thread
  still never calls the foreign application.
- Phase 4 can build the file-browser pane without reopening foreign-window hosting architecture.

## What failed

- The first Calculator attach check requested 640×480 and failed its exact-height assertion. The
  app enforced a larger minimum height, confirming the Phase 0 warning that requested and actual
  rectangles need not be equal. The verifier now uses 720×680, above the measured minimum; product
  layout still treats application minimum sizes as a compatibility limitation.
- The first wrapper around the passing Character Map verifier inspected PowerShell's stale
  `$LASTEXITCODE` after invoking another script and falsely reported failure. Script exceptions are
  now allowed to propagate directly; `$LASTEXITCODE` is used only for actual executables.
- Physical mixed-DPI multi-monitor behavior remains **unmeasured** because both available displays
  are 144 DPI. PerMonitorV2 and mixed hosting are implemented, and Phase 0 measured cross-awareness
  hosting at one 150% scale, but no claim is made about crossing different monitor scales.
- Higher-integrity Task Manager attach was not rerun. Its quirks rule selects attach and any
  positioning refusal is visible, but UIPI may forbid useful control from a normal-integrity host.
  WinMux will not elevate itself to bypass that boundary.
- Input-queue starvation is still not directly measurable with the available harness. PaneHost
  therefore remains a true top-level owned window; it is never reparented into the shell.
