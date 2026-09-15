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

---

## Addendum, same day — native Windows 11 appearance

The chrome above was consistent but invented. Three things make an app read as foreign on Windows 11
before anyone examines a single control, and it had all three: it ignored the light/dark setting,
it chose its own accent instead of the user's, and it painted a flat opaque background where the
system shows Mica.

- **The theme variant is read from `IPlatformSettings.GetColorValues()` and set explicitly**, not
  left at `ThemeVariant.Default`. Default did not resolve the platform here: Fluent kept using its
  light control colours underneath our dark brushes.
- **The accent is the user's**, lightened for dark mode and darkened for light, never a literal.
- **Mica**, via `TransparencyLevelHint`, with `AcrylicBlur` and then `None` behind it. Confirmed
  active on this machine by reading back `ActualTransparencyLevel`. The chrome brushes carry alpha
  so the backdrop shows through; the shell says so in the status bar if Windows refuses it.
- **Brushes are mutable singletons whose `Color` is mutated in place.** Styles capture a brush
  reference once, so swapping the objects on a theme change would leave everything already on
  screen painted in the old scheme.
- **Icons are drawn geometry, not a symbol font.** Segoe Fluent Icons is the right look, but a
  wrong codepoint renders as a hollow box and a machine without the font renders every icon as one
  — a failure that looks like a broken application and that no test we run would catch.
- The restore notice, the first thing seen when opening a session, became a real dialog: heading,
  body, shaded action footer, Esc to dismiss. Dialogs use **opaque** surfaces, because a dialog has
  no backdrop of its own and the translucent chrome brushes let the panes read through the text.
- The file browser's hardcoded palette was replaced with the shared one it had drifted from.

### What failed here

- **Reading a screenshot is not measuring.** The active tab looked like a light card with dark text
  and I nearly rewrote the style resolution over it. Sampling the actual pixel gave `#3A3A3A` —
  exactly the brush it was supposed to be. The rendering was right and the reading was wrong; a
  runtime dump of the resolved palette settled it in one run.
- **`dotnet build` on the shell project alone writes to `bin\Release\`, not `bin\x64\Release\`**,
  because the project is x64-only through the solution's platform mapping. Two rounds of "my change
  had no effect" were actually a stale exe. Build the solution, or pass `-p:Platform=x64`.
- The accent strip was added only to the active tab, which made it two pixels shorter than its
  neighbours and shifted the row as the selection moved. It is now on every tab, transparent when
  inactive.

---

## Addendum — dragging the window ran at one frame per second

Reported as "like 5fps" when moving the window. Measured: **60 programmatic moves took 70.0 s —
0.9 moves per second**, on the five-pane `tabs-and-splits` session.

The cause was not rendering. `PositionChanged` called `Relayout()`, which ended in
`SessionController.RequestSave()`. The autosaver debounces the *write*, but the **snapshot was
captured synchronously first**, and capturing asks every pane for its restore descriptor — which a
terminal pane answers by walking the machine's entire process list and reading a PEB (ADR 0004,
strategy 2). One Toolhelp32 snapshot measured **120 ms against 334 processes**, and the resolver
does one per pane plus a fallback. Dragging the window enumerated every process on the system,
several times, per mouse move.

Two fixes:

1. **`RequestSave` is now cheap and coalescing.** It starts a 400 ms `DispatcherTimer`; the
   snapshot is taken once the caller stops asking. `SaveNow` still captures immediately, so
   shutdown and explicit saves are unchanged.
2. **A window move no longer relayouts.** Moving the window changes no pane rectangle, so
   rebuilding every tab strip was pure waste at mouse-move frequency. `FollowWindowMove` re-asserts
   foreign window placement only — the one thing that must follow, because a foreign pane's host is
   a separate top-level window positioned in *screen* coordinates.

**After: 60 moves in 1.34 s — 44.8 moves per second.** Same session, same harness: **52× faster**.

The general lesson is worth keeping: *debouncing the write does not help when producing the input
is the expensive part.* The autosaver looked correct in isolation and was.

### Also fixed here

- **Tab labels were clipped along the baseline.** A 28-pixel strip could not hold a 12px line plus
  the tab's padding, its accent strip and the strip's own border. The strip is 34 now, sized from
  what has to fit, with the paddings trimmed to match.
- The focused strip drew a full-width two-pixel accent slab along its content edge, far louder than
  Windows draws anything. It is a one-pixel hairline again; the focused tab's own accent already
  says which group has focus.

---

## Addendum — the process walk was on the interactive path, not just the drag

The previous addendum moved the process walk off the *window drag*. It was still on the path of
**every terminal title change**: `runtime.StateChanged` → `SyncRuntimeState` →
`CaptureRestoreDescriptor()` → `RefreshRestoreState()` → a full snapshot. A shell emits title
updates as you use it, so with eight panes open the UI froze constantly.

Measured, on this machine with ~330 processes:

| | cost |
|---|---|
| `SnapshotProcesses` | **123 ms** |
| `TryReadWorkingDirectory` | **0.12 ms** |
| one capture pass, 7 terminal panes | **821 ms** |

The snapshot is a thousand times the price of the answer it enables, and it was being paid per
pane, per event.

`CachedProcessInspector` decorates `IProcessInspector`: one recent process list shared by every
caller, served immediately and refreshed in the background past a 5 s TTL. Only the snapshot is
cached — `TryReadWorkingDirectory` goes straight through, so the *directory* is always read live
from whichever process the tree points at. Staleness therefore degrades rather than lies: the worst
case is choosing a pane's root over a child spawned moments ago, which is the answer we would give
if that child did not exist, and it corrects on the next pass. A failed refresh keeps the last good
list, because "every pane's process has vanished" is a much worse answer than a few seconds of age.

**End to end, on the reported eight-pane session: 60 window moves in 59 ms — 1,014 per second,
against 0.9 per second when this was first reported.**

The tests assert call counts, not elapsed time: a timing assertion is flaky on a busy machine, and
"how many times did we ask the operating system" is the property that matters.

Still true and still worth fixing one day: `CreateToolhelp32Snapshot` at 123 ms is simply a slow
call. `NtQuerySystemInformation(SystemProcessInformation)` returns the same facts in single-digit
milliseconds. The cache makes that a background cost rather than an interactive one, so it is no
longer urgent.

### Save As

The layout could be saved but not saved *elsewhere*. `save-session-as` opens a file picker and then
**keeps working in the new file** — Save As, not "export a copy": the controller flushes the old
autosaver, replaces it, and moves `SessionPath`. Flushing first matters, because a pending debounced
write against the old path would otherwise be lost, which is precisely what priority 1 forbids.
It is on the Save button's dropdown, in the palette and in the CLI, with no key binding.

---

## Addendum — open, settings, and a resize affordance

Three gaps found by using the app, all of the same kind: the capability existed, the interface
did not.

- **Pane resizing has worked since ADR 0005** and nobody could tell. The gutter was six pixels of
  window background with no handle, no hover state and no cursor. `DividerHandle` draws a grip and
  sets a resize cursor; it handles no events, because `MainWindow` already owns the drag against
  the arrangement's divider rectangles and two owners would eventually disagree about where a
  divider is.
- **Open session** replaces the layout on screen. It validates the file before closing anything, so
  a file that will not parse leaves the current session untouched, and it detaches foreign apps
  rather than killing them — they were adopted, and adopting something is not a licence to close
  it. `SwitchFileAsync` is deliberately not `SaveAsAsync`: the old layout belongs in the old file,
  flushed there before the switch, and the newly opened file is left as it was on disk.
- **Settings** persist to `%APPDATA%\WinMux\settings.toml`. Four entries, each changing something
  visible: theme, default terminal, where a new tab group puts its tabs, and whether an action that
  closes running panes asks first. Unlike a session, an unreadable settings file never stops WinMux
  starting — it falls back to defaults and reports *which key* was wrong, because starting
  differently without saying so is the worse failure.

### What failed

- **The right-aligned toolbar group did not render, and I could not find out why.** Save, Settings
  and the palette were a separate group docked to the right. They painted nothing — not the
  buttons, and not a debug background on the panel itself — while `LayoutUpdated` reported the group
  visible at sensible bounds (`1357, 6, 119×28`) with three children. A `DockPanel` with the group
  docked first, a `Grid` with an `Auto` column, and removing the explicit `HorizontalAlignment` all
  behaved the same. The identical controls render correctly when added to the main `StackPanel`,
  which is where they now live — and where Windows Terminal keeps its equivalents. The toolbar row
  scrolls when the window is narrow, so a button can be off-screen but never silently absent.
  Shipping chrome that depends on unexplained behaviour was the worse option; this is recorded as
  unexplained rather than fixed.
- **Screenshot-driven debugging has a cost I underestimated.** Capturing the app meant repeatedly
  taking the foreground on a machine somebody was using, and one capture returned their browser
  instead of WinMux. Pixel sampling also produced a false positive — a "present" reading that was
  the tail of the *Close pane* label, which sent the diagnosis in the wrong direction for a while.
  Instrumenting the layout and reading the numbers settled in one run what four screenshots had not.
