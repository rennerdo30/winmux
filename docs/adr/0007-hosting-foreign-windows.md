# ADR 0007 — Hosting foreign windows: `NativeControlHost`, and embed only

- **Status:** superseded by [ADR 0008](0008-pane-host-ipc.md)
- **Date:** 2026-09-10
- **Code:** `WinMux.Shell` — `ForeignHost`, `ForeignAppPane`, `Embedding`, `MainWindow`

## Context

Phase 1 needed the shell to actually host applications. ADR 0001 had established that the UI thread
must never make a synchronous window call against a foreign window, and had left the input-queue
transitivity question open, so the first implementation used **attach mode**: the application stays
a top-level window and is driven to follow the pane rectangle from a background thread.

It worked, and it was wrong. The user's words: *"the explorer window does not seem to be attached,
only following our window"* — exactly right. The app kept its own title bar and close button, sat
above the shell rather than inside it, and separated on alt-tab. That is not containment, and the
README promises containment. Then: *"attach mode was an option you brought up, for me this is not
an option! all applications need to be properly embedded"*.

## Decision

**1. Embedding goes through Avalonia's `NativeControlHost`. Never a hand-rolled `SetParent` into
the shell window's HWND.**

This is the finding that cost the most and is the least obvious. Hand-parenting *appears* to work:
`SetParent` succeeds, `GetAncestor(GA_PARENT)` confirms the new parent, the window is sized and
positioned to the pane rect, `IsWindowVisible` returns true, and enumerating the shell's children
shows the app's entire control tree laid out at the right screen coordinates.

**And it paints nothing.** Avalonia renders through a composition swapchain; a child HWND parented
in by hand is never composited into it. The pane shows whatever was on screen behind the shell.
Measured with Character Map — a plain GDI Win32 dialog, so this is not a DirectComposition or
WinUI quirk, it is the rule.

`CLAUDE.md` §2 already said so ("Avalonia's `NativeControlHost` is purpose-built for embedding
native handles") and named the same failure as the reason Tauri/WebView2 was rejected. It was
there to be read.

**2. `DestroyNativeControlCore` must be overridden to do nothing.** The default destroys the
handle, and the handle belongs to another application.

**3. Strip the frame explicitly.** `SetParent` does not fix styles. Clear `WS_CAPTION`,
`WS_THICKFRAME`, `WS_SYSMENU`, `WS_MINIMIZEBOX`, `WS_MAXIMIZEBOX`, `WS_BORDER`, `WS_DLGFRAME`, then
`SWP_FRAMECHANGED`. Skipping this is precisely what "following" rather than "embedded" looks like.

**4. Embed only. Attach is not a runtime fallback.** When the OS refuses — UIPI returns
`ERROR_ACCESS_DENIED` (5), UWP returns `ERROR_INVALID_PARAMETER` (87), both measured in spike 2 —
the pane says so in plain words rather than silently degrading into a floating window.

**5. Detach before the shell window is destroyed, always.** A parent takes its children with it.
`MainWindow.Shutdown` detaches every embedded window on a bounded background thread before close.
Verified: after a graceful close Explorer reappears as a normal top-level window with its title bar
restored.

**6. Window selection needs a "reuse" fallback.** `explorer.exe <folder>` with a window already
open on that folder activates the existing one; nothing new appears and waiting for a new window
waits forever. After a timeout, adopt an existing unclaimed window matching the rule, and track
claimed HWNDs so two panes cannot take the same one.

## Consequences

- Terminal panes and foreign-app panes are both Avalonia controls now, so the layout engine drives
  them identically and `ForeignWindowTracker` is no longer on the critical path for embedded panes.
  It stays for the not-yet-embedded case and for anything that must be positioned as a top-level
  window later.
- ADR 0001's rule still holds and still matters: the tracker thread exists because a synchronous
  window call against a wedged app blocks the caller. What Phase 1 adds is that the *shell* window
  is now the parent, which is the topology spike 3 called T2 — the one that froze when window
  calls came off the UI thread. Keeping them off it is doing the work.
- The out-of-process `WinMux.PaneHost` from §5 is still not built. Until it is, a wedged embedded
  app can stall foreign-window operations. That is the remaining gap between this and the design.

## What failed

- **Attach mode was built first and was the wrong reading of the requirement.** ADR 0001's
  constraint is about *synchronous calls from the UI thread*, not about reparenting; I generalised
  it into "don't reparent" and shipped something that visibly was not a multiplexer.
- **Four repaint fixes were tried against the wrong diagnosis.** `RedrawWindow` with
  `RDW_ALLCHILDREN | RDW_UPDATENOW`, `SWP_NOCOPYBITS`, an off-by-one resize nudge, and a hide/show
  cycle — all reasonable, all useless, because the window was never being composited at all. The
  giveaway was there early and misread: the *chrome* of Explorer painted while its content did not,
  which looked like a DirectComposition problem. Character Map — pure GDI — failing identically is
  what disproved that.
- **The bug was diagnosed three times from screenshots before being measured.** Enumerating the
  shell's child windows settled it in one call: correct parent, correct rect, visible, invisible.
  Reading pixels off a screenshot to infer window geometry wasted several rounds.
- **Development repeatedly destroyed the test subject.** Force-killing the shell between builds
  skips detach and destroys embedded windows, leaving windowless `explorer.exe` orphans that then
  broke the *next* run's window selection. Spike 3 documented exactly this and it still had to be
  rediscovered live. Close the shell with `WM_CLOSE`.
