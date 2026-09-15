# ADR 0016 — Looking like Windows 11

**Date:** 2026-09-15
**Status:** Accepted
**Supersedes nothing.** Extends [ADR 0014](0014-nested-tab-groups-and-shell-chrome.md).

## Context

The chrome had Mica, the system accent, light/dark following the OS, vector icons and rounded
corners, and it still did not look like a Windows 11 application. The complaint was not about any
one control, which is the useful part: something was wrong that survived getting the colours right.

Two things were.

**The type ramp was one step small everywhere.** Body text was 12px where Fluent 2 sets 14, captions
were 11 where it sets 12, and buttons came out about 26px tall against WinUI's 32 minimum. Nothing
looked broken; everything looked slightly dense, which is what a well-maintained WPF application
from 2012 looks like. Colour is the thing people name and size is the thing they see.

**The window had a system title bar above its own toolbar.** Explorer, Settings, Terminal and Edge
all put their controls *in* the caption row. Two stacked bars is visible before any content is, and
no amount of styling below it helps.

The settings page was the same mistake in miniature: a label in the left column of a grid and a
combo box in the right, which is a Windows 7 control-panel idiom, where Windows 11 uses a rounded
card carrying a label, a line of explanation, and the control.

## Decision

**1. `Palette` carries Fluent 2's ramp, and chrome takes it rather than inventing a size.**
`CaptionSize` 12, `BodySize` 14, `SubtitleSize` 20, `ControlHeight` 32, `GapSmall/Medium/Large`
8/12/20, `ButtonPadding`. A `dialog-button` style class carries the metrics so six dialogs stop
restating them. The horizontal tab strip grew 34 → 40 and the vertical one 180 → 220 to hold 14px
text, which is a layout change, so it lands in `LayoutMetrics` and `Layouter` together.

**2. The window draws its own caption.** `Window.WindowDecorations = BorderOnly` leaves Avalonia
drawing the border, drop shadow and the eight resize grips — tedious, scaling-dependent, easy to get
wrong — and stops it drawing the title bar. `Chrome/TitleBar.cs` supplies that: app icon, window
title, the toolbar, a drag region, and minimise / maximise / close.

Avalonia 12 changed this model from 11. There is no `ExtendClientAreaChromeHints` enum any more;
there is `WindowDecorations` and `Avalonia.Controls.Chrome.WindowDrawnDecorations`, which has a
`Content.Overlay` intended for exactly this. The overlay was not used because `Content` has no
public setter and reaching it means supplying a whole `IWindowDrawnDecorationsTemplate`; drawing
three buttons was smaller. Revisit if that property opens up.

**3. Caption glyphs are geometry, like every other icon** (`Icons.CaptionMinimise` and friends).
Segoe Fluent Icons is the right look and a machine without it renders every glyph as a hollow box —
tolerable for a toolbar, not for a close button.

**4. The settings page is `Chrome/SettingsCard.cs`**: a rounded surface with a border, a label, an
optional description and the control, plus a sentence-case group heading. WinUI ships this as
`SettingsCard`; it is reproduced rather than taken, because depending on the WinUI control library
means giving up Avalonia and the portability CLAUDE.md section 1 keeps open.

## Consequences

- **Snap layouts had to be bought back.** Hovering a maximise button shows Windows 11's snap
  flyout, and the system finds that button by asking the window through `WM_NCHITTEST` — which an
  app drawing its own caption never gets asked. `ISnapLayoutService` (added 2026-09-15) is the
  capability that answers; see the addendum below.
- Dragging the window and double-click-to-maximise stop being free and are handled on the row.
- A maximised window with an extended client area is larger than its monitor by the resize border;
  the root takes `OffScreenMargin` as its margin so the caption buttons stay reachable.

## What this cost, and the finding that is worth more than the feature

Most of the session went on a bug that was not in the application.

The caption buttons laid out correctly — a visual-tree dump had them at the right size, position,
visibility and stroke colour — and did not appear in a screenshot. Nor did a magenta background on
their container. The same symptom had been recorded in ADR 0014 as an unexplained Avalonia failure
("a right-aligned toolbar group reports sensible bounds and paints nothing"), which made it look
like a known platform problem being hit again.

Rendering the control in-process to a `RenderTargetBitmap` settled it: Avalonia drew the buttons,
5,482 magenta pixels exactly where layout said they were. The pixels were being lost after the
renderer.

**The screenshot harness was PowerShell, which is DPI-unaware.** On this 150% display every
coordinate it saw was virtualised: `GetWindowRect` reported 1500x900 for a window that was really
2250x1350, and `CopyFromScreen` captured the wrong region of the screen. One
`SetProcessDpiAwarenessContext(-4)` and the buttons were there, and had been all along.

Three rules follow, and they are in HANDOFF.md:

- A screenshot is evidence only from a DPI-aware process.
- When layout and pixels disagree, render in-process before blaming the framework.
- Do not resize an Avalonia window with `SetWindowPos` from outside it; it goes on laying out at the
  size it believes it has.

The ADR 0014 note should now be read as probably describing the same measurement error rather than
an Avalonia bug. It has not been re-tested, so the toolbar's buttons stay where they are.


---

## Addendum, 2026-09-15 — snap layouts, and what claiming a caption button costs

`ISnapLayoutService` lets the shell say "this rectangle is my maximise button", and
`Win32SnapLayoutService` answers `WM_NCHITTEST` with `HTMAXBUTTON` over it. The flyout is back,
verified on screen.

There is no API for this. The only way to be offered the flyout is to answer the hit test the way a
window with a system caption would, so the implementation subclasses the window procedure — keeping
the previous one and restoring it on dispose, and passing through every message it does not claim.

**Claiming the rectangle takes the button away from the application**, and all three consequences
have to be handled or it stops working:

* Windows stops sending ordinary mouse messages over that area, so Avalonia's `Click` never fires.
  The click arrives as `WM_NCLBUTTONUP` instead.
* `WM_NCLBUTTONDOWN` must be swallowed. Left to the default handler it begins a caption drag, and
  the window moves when you press maximise.
* Hover is the system's to report now, through `WM_NCMOUSEMOVE` and `WM_NCMOUSELEAVE`.

The last one has a trap that crashed the app on first run: Avalonia refuses to let anything but a
control set its own `:pointerover` and throws `ArgumentException` when you try. The system-driven
state therefore needs a class of its own — `Theme.CaptionHover` — styled to match.

The contract is stated as intent, so a platform with no such concept returns null from `Track` and
the application keeps working with one affordance fewer.
