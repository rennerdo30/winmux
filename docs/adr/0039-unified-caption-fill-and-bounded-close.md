# ADR 0039 — Unified caption fill and bounded close save

**Date:** 2026-10-12
**Status:** Accepted, owner visual check open

## Context

The 0.8.0 title bar painted three backgrounds. Its translucent `RaisedBrush` lay once behind the
caption buttons and twice behind the toolbar. The root tab band used an opaque `TabStripBrush`.
On a dark 150% display, the owner could see a different shade behind the window buttons, plus a
horizontal edge below the caption. The same layers existed in light mode. WM-1 moved root tabs
into the caption and made the mismatch conspicuous; WM-13 changed the inactive backdrop but did
not cause the duplicate toolbar layer.

Commit `5fb3cd8` had already removed a *second* toolbar separator after it made the middle of
the caption thicker than the ends. That fix left the title bar's own bottom border in place.
The later root-tab move exposed that remaining edge and the two translucent fills.

An unrelated 0.8.0 shutdown review found that cancelling a token cannot interrupt an already
running synchronous filesystem write. A ten-second token therefore did not bound the close wait.

## Decision

The title bar, the resting caption-button presenters and the root tab band share the one opaque
`Palette.TabStripBrush`. The toolbar adds no fill. The title bar and root caption strip add no
bottom separator. Hover and pressed button presenter styles continue to override only those
states. Nested tab strips retain their content edge.

The close path separately bounds its await. A timed-out write keeps the window open while the
existing atomic temporary-file replacement finishes or fails. A late fault is observed. No
deadline aborts a write halfway through replacing a session file.

## Verification and limits

Headless tests assert the shared caption token, no root caption separator, and unchanged nested
edge. A blocked-writer test checks that the close deadline returns while the old session file
remains complete, then verifies that the eventual atomic replacement is complete. Native 150%
dark/light focused/unfocused captures and caption hit tests are recorded on WM-17. Both physical
monitors are at 150%; 100% native appearance remains unmeasured pending an appropriate display.

## What failed

The first caption change set `Button.Background` directly. That violates the chrome rule because
Fluent animates the templated `ContentPresenter`; the final change sets the presenter's resting
fill through a style. The first headless test also assumed that the shared palette had already
been initialised, although the test app deliberately leaves platform colours unset. It now tests
token identity, while native captures verify actual colours.
