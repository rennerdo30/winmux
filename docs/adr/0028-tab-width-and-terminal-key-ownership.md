# ADR 0028 — Tab strip width and terminal key ownership

- **Status:** accepted; native acceptance open
- **Date:** 2026-10-07
- **Spike:** none

## Decision

Vertical tab strips use compact aligned rows and a shared Add/More footer. Their content-facing
edge can be dragged independently of split dividers. Each StackNode stores an optional width,
clamped to 96–480 DIP, serialized as tab_width with its existing node. Older sessions retain
the default metrics. Layout reserves the strip inside the group bounds and shrinks the displayed
width in small windows without discarding the saved preference. Core contains no platform types.
Retained strip controls keep pointer capture and scroll position through resize refreshes.

Converting an explicitly named tab to a group or split preserves its name on the wrapper;
renaming the inner pane thereafter cannot change that parent label. Automatic titles remain
derived. Cancelling a rename never clears the previous explicit label.

Saved connections filters immediately by case-insensitive words across names, folder paths,
resolved host/user/domain/protocol fields and source names. Matching ancestors stay visible;
clearing restores the full tree. No matches disables actions and shows an empty-state message.
Search reads no credentials. Ctrl+F focuses the search field.

The user requested Ctrl+C always mean interrupt. Terminal panes therefore reserve clipboard
actions for exact Ctrl+Shift+C/V and Ctrl+Insert/Shift+Insert chords. Plain Ctrl+C, Ctrl+V and
Escape reach the application, even with a selection or clipboard text. Escape may also clear
the visual selection. Alt+V stays application input. History shortcuts use exact modifier sets,
so adding Ctrl/Alt does not accidentally invoke a different shell action. Custom shell keymaps
remain explicit overrides; the default shell command prefix remains Ctrl+Shift+P.
The clipboard chords follow the documented alternate bindings in
[Windows Terminal actions](https://learn.microsoft.com/windows/terminal/customize-settings/actions).
Windows Terminal also supports Ctrl+V for paste; WinMux deliberately leaves it to applications
under this user's request to preserve application controls.

## Validation and limits

Regression checks cover persisted independent widths, older files, invalid bounds, left/right
layout, group name roundtrips, inner Rename targeting, resolved search fields and real terminal
control input with a selection and clipboard text. Skia captures at 150% show compact aligned
rows; real headless pointer drags verify both resize edges, pointer capture, retained scroller
and release behavior. A missing detachment when wrapping the scroller was caught and fixed
by these integration tests before publication.
The filtered connections capture uses an in-memory fixture. Native Windows pointer/DPI feel and live CLI application
acceptance remain open; captures use in-memory sources and do not inspect the user's credentials.

## What failed

The fixed vertical width wasted pane space; generic Fluent button padding made rows oversized
and Add/More occupied separate rows. A newly wrapped named pane had an unnamed parent, causing
its parent label to track the first child. Cancelled rename could clear an inherited label.
Plain Ctrl+C copied selected text, Ctrl+V depended on clipboard contents, and Escape was swallowed
by a selection. Broad modifier checks intercepted chords containing additional modifiers.
