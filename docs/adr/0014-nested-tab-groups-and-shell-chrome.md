# ADR 0014 — Nested tab groups, tab placement, and a real toolbar

- **Status:** accepted
- **Date:** 2026-09-15
- **Code:** `WinMux.Core/Layout`, `WinMux.Core/Session`, `WinMux.Shell/Chrome`
- **Acceptance:** `dotnet test WinMux.slnx -c Release` — 344 passed, 0 warnings;
  `examples/tabs-and-splits.toml`

## Context

Everything WinMux could do was reachable only by a tmux prefix. That is the right *fast* path and
the wrong *only* path: a prefix has to be told to you, and an application whose entire interface is
undiscoverable keystrokes is a terminal program wearing a window.

The tab story was worse than undiscoverable, it was structurally wrong. `StackNode` has allowed
tabs anywhere in the tree since [ADR 0005](0005-layout-engine.md) — `AddTab` wraps any leaf,
including one inside a split or inside another stack — but the shell drew **one tab bar, docked to
the top of the window**, showing whichever stack happened to enclose the focused pane. So a tree
with two tab groups side by side rendered one bar, in the wrong place, describing one of them. The
model supported nesting; the UI could not express it, and therefore nobody could use it.

## Decision

1. **A tab strip is geometry, emitted by the layout engine.** `Layouter` reserves a band out of
   each `StackNode`'s own rectangle *before* placing its active child, and reports it as a
   `TabStrip { Stack, Rect, Placement }` on the `Arrangement`. Every stack gets one, wherever in
   the tree it sits.

   This is not a cosmetic choice. CLAUDE.md section 6: a pane hosting a native window paints above
   anything Avalonia draws. Tabs rendered *over* a pane would vanish behind Explorer the moment a
   foreign app opened in it. Reserving the band first makes "strip and pane never overlap" a
   property of the layout rather than a hope, and `TabStripLayoutTests` asserts it on all four
   edges.

2. **Placement is per stack: `Top`, `Bottom`, `Left` or `Right`**, stored on `StackNode` and
   persisted. Per stack rather than per window, because a stack of long titles wants a vertical
   strip while its sibling three inches away does not.

3. **A vertical strip is much wider than a horizontal one is tall** — 180px against 28px. The
   sizes live in `LayoutMetrics`, replacing the loose `int?` thickness parameters, because a
   vertical strip lists titles down the side and 28 pixels of truncated text would be the feature
   in name only. `LayoutMetrics.CharacterGrid` is how the CLI asks for no strips at all.

4. **`tabs` is written to the session file only when it is not the default.** The file is meant to
   be hand-edited (ADR 0006), and every stack carrying `tabs = 'top'` would be noise in the part
   that matters most to read. An absent key loads as `Top`, so sessions written before this ADR
   load unchanged; a misspelled one names the offender rather than quietly defaulting.

5. **The shell grows a toolbar and per-stack strips.** The toolbar carries new terminal (with a
   profile dropdown), file browser, split right, split down, tab this pane (with placement
   options), close pane, save and the palette. Each button dispatches **the same named action** as
   the keymap, palette and CLI, so there is one implementation of "split into columns" and four
   ways to ask for it. Each strip carries its tabs, a `+`, per-tab close, and a menu for moving the
   strip. Both live where panes never do.

6. **`move-tabs-*` ship with no key binding.** They are reachable from the toolbar, from every
   strip's menu, from the palette and from the CLI; they are not worth a prefix key apiece. The
   keymap test that demanded a binding for every action was rewritten to demand a binding *or*
   membership of an explicit menu-only list, with two further tests so the exemption cannot rot
   into a typo or silently cover a bound action.

7. **Chrome styling is centralised** in `Palette` and `Theme`, applied as Avalonia styles rather
   than properties set per control.

## Consequences

- Nested tabbing is now usable: a tab group inside a split, a tab group inside a tab group, each
  with its own strip in its own place. `examples/tabs-and-splits.toml` is the shape.
- The layout engine owns one more thing, which is correct — where tabs go changes where panes go,
  and that was the bug.
- Strips are rebuilt wholesale on every layout rather than diffed. They are small and there are as
  many as there are stacks, usually one or two, so the cost is nothing next to keeping a second
  model of the tree in sync with the tree. It does mean a divider drag rebuilds them per pointer
  move; if that ever shows, diffing by `StackNode` identity is the fix.
- The active tab's rectangle shrank by the strip's thickness. `ArrangementTests` had asserted the
  old behaviour and was updated deliberately, not silenced.
- Tab *reordering* is still not implemented, and neither is dragging a pane between groups. The
  strips are the surface those will attach to.

## What failed

- **`Background = Brushes.Transparent` on a Button silently kills Fluent's hover and pressed
  feedback**, because the template animates the very property being overwritten. The first toolbar
  looked right in a screenshot and felt dead under the pointer. Fixed by moving to styles that
  target the templated `ContentPresenter`, which is where Fluent actually paints the fill.
- **A 28-pixel vertical strip** was the first implementation, reusing the horizontal thickness. It
  produced a column of ellipses. That is what forced `LayoutMetrics`.
- **`TabStrip` collides with `Avalonia.Controls.Primitives.TabStrip`**, and `Rect` with
  `Avalonia.Rect`, in the same shell. Both are aliased at the point of use. Second time this has
  happened (ADR 0013); assume any short geometric or widget-shaped name in Core has an Avalonia
  namesake.
- **The first `tabs-and-splits.toml` set `cwd_source` with no `cwd`**, which the reader correctly
  refused — and, being a load failure, it quarantined a `.corrupt-*` copy into `examples/`. The
  validation did its job; the example was wrong.
- **The clamp for a squeezed stack was wrong in a way the test caught**: it shrank the strip to fit
  rather than dropping it, so a 10-pixel-tall stack got a 9-pixel strip and a 1-pixel pane. The
  rule is now full thickness or nothing — the panes are what the user is looking at.
