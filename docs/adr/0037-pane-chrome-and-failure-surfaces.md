# ADR 0037 — Pane chrome and failure surfaces

**Date:** 2026-10-12  
**Status:** Accepted for owner visual review

## Context

The owner found the tabs cheap looking (WM-1). The same review found unnamed plain split panes
(WM-15), a hard-coded dark blue foreign-app failure pane (WM-12), and translucent canvas gutters
that show the desktop when Mica deactivates (WM-13). These share the shell's chrome layer. Foreign
applications paint above Avalonia, so labels and actions cannot overlap a pane rectangle.

## Decision

Horizontal tabs use a 34 DIP, bounded-width row with a clear selected surface, a narrow focus
pill, neutral close actions, and fixed add/menu buttons. Overflow has explicit scroll controls.
Vertical tabs retain their compact list layout. The toolbar uses icons and tooltips for routine
actions; close and rename live in the pane header menu or tab menu. Plain leaves reserve a 28 DIP
header in their layout rectangle, including the native-window placement rectangle. A stack's own
reserved tab strip continues to provide its title, so it gets no duplicate header.

The canvas uses an opaque theme brush. The top-level window permits Mica only while active and
available, then uses that opaque brush. The foreign-app failure surface uses palette brushes and
the saved program's icon, with explicit Launch new, Open outside WinMux and Close pane actions.

## Verification and limits

The local Release app ran with isolated dark and light profiles. Screen captures show horizontal
tabs, two plain split panes, and a failed Character Map pane in both themes. An inactive dark and
light split capture sampled the divider gutter at `#1C1C1C` and `#F3F3F3`, respectively; the
runtime log also recorded inactive Mica fallback. The overflow control has a UI test that scrolls
12 tabs at a 480 DIP window width. Native screenshots are attached to WM-1, WM-12, WM-13 and
WM-15 for owner review. The full matrix of nested stacks and foreign apps still needs owner
inspection; these captures are representative, not exhaustive.

## What failed

The headless renderer returned no bitmap for a visual assertion, so it could not establish
pixel-level acceptance. A native screen capture of this job's own window replaced it. An initial
focus pill was inserted into side tabs too and broke their aligned labels; the existing side-tab
test caught it, and the pill is now horizontal-only. A first screenshot also exposed status text
overlapping the right-side session field; the left field now has constrained grid columns.
