# ADR 0038 — Root tabs in the custom caption

**Date:** 2026-10-12  
**Status:** Accepted for owner visual review

## Context

The sole root stack put a 40 DIP tab band immediately below a 40 DIP custom caption. That
doubled the chrome height and made the tab design look detached from the window. Nested stacks
still need their own reserved bands because they can be inside splits and above foreign windows.

## Decision

Only a root stack with top tabs moves its strip into the caption's flexible center column. The
layout engine lays out that stack's active child over the full pane canvas and continues to reserve
bands for every nested stack. Other root shapes and bottom or side placements keep the toolbar in
the caption. The caption's identity and native-size minimize, maximize and close buttons remain in
fixed columns. A tab press is handled by its button; blank caption space remains the window drag
target. Double-clicking blank caption space still toggles maximize.

The selected focus pill slides for 167 ms; hover fill uses 83 ms; inserting or closing a tab
uses a 167 ms width/opacity transition. A dragged tab shows a 90% opaque preview with a shadow,
while the destination's neighbors make a 20 DIP gap over 150 ms. All those transitions turn off
when Windows disables client-area animation. The destination caret and tab drag work without
motion as well.

## Verification and limits

Release builds and headless tab tests pass. Native dark and light captures show the integrated
caption, drag preview, snapped-layout hover and maximized frame. In both themes, a press-drag on
the tab left the window fixed; a blank-caption drag moved it by 73,55 physical pixels; the
maximize button returned `HTMAXBUTTON` (9) and toggled maximize; minimize entered iconic state;
the close hit target exited this job's window. The Windows snap layout flyout appeared on hover.
The full nested/foreign matrix and visual feel still need owner inspection.

## What failed

The first drag-preview wrapper attempted to add a control before removing it from its parent;
Avalonia rejected the second visual parent. Detaching before wrapping fixed the startup crash.
The first native pointer probe used physical screenshot coordinates from a DPI-unaware process,
so its clicks landed below the caption. Making that process per-monitor DPI aware fixed the
probe; the final receipts use physical coordinates throughout.
