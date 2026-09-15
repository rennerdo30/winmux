# ADR 0015 — Profiles, the application catalogue, and empty panes

- **Status:** accepted
- **Date:** 2026-09-15
- **Code:** `WinMux.Core/Settings`, `WinMux.Platform`, `WinMux.Platform.Win32/Windows`,
  `WinMux.Shell`, `WinMux.PaneHost`, `assets/`
- **Acceptance:** `dotnet test WinMux.slnx -c Release` — 398 passed, 0 warnings

## Context

WinMux could host any Windows application in a pane from Phase 3, and there was no way to ask it
to. Foreign-app panes existed only if you hand-wrote a TOML file. The settings dialog was four
switches, one of which chose a "default terminal" from a list the user could not see or edit — the
question *which terminals do I have* had no answer anywhere in the product.

That is the same failure as pane resizing having no handle (ADR 0014): **a capability with no
interface is not a feature.** CLAUDE.md section 5a now states the rule, and this is the work that
follows from it.

## Decision

1. **A profile is a named thing you can put in a pane.** One concept covers terminals and
   applications, because to the layout they are identical: a program, its arguments, a working
   directory, and — for a windowed application — how to find and host its window. `LaunchProfile`
   lives in Core beside the session model, and `ProfilesFile` persists the list to
   `%APPDATA%\WinMux\profiles.toml`.

   The shipped terminals plus File Explorer are **seeded, not special**. They are ordinary
   entries, editable and deletable, and nothing else in the product treats them differently.

2. **`IAppCatalog` is how a user finds an application without knowing where it lives.** Windows has
   no API that lists installed applications; the Start Menu is the closest thing, and it is a tree
   of `.lnk` files that must be opened through COM to learn their targets. Both Start Menu roots are
   read. Measured on the development machine: **147 applications in 199 ms**.

   Entries that resolve to nothing runnable are dropped, along with uninstallers and help links: a
   picker that offers something which cannot start is worse than a shorter list.

3. **An empty pane is a first-class state**, with its own `PaneKind` and provider. It offers the
   profile list, the application catalogue, and the windows already open. This is what makes
   "split first, decide after" a supported workflow rather than a gap, and it is the honest landing
   place for an adopted window that could not be restored.

4. **`LayoutTree.ReplacePane` swaps a pane in place.** The layout does not move — the replacement
   takes the old pane's exact position, its share of its split and its place in any tab group. That
   is what makes an empty pane become something without disturbing the arrangement around it.

5. **A window that is already open can be taken into a pane.** `IWindowCatalog` lists candidates,
   applying ADR 0003's selection rules as hard as they are applied when launching — Notepad alone
   owns twelve top-level windows and eleven are not the one anybody means. Measured: **13 real
   windows in 9 ms** on a busy desktop. `WinMux.PaneHost` gains `--adopt <hwnd>`, which skips
   launching and discovery entirely: the window is known.

   An adopted pane stores **the handle as a launch instruction and the program path as the restore
   descriptor**. This session takes the window that is there; the next session starts the same
   program rather than chasing a handle that will not exist. When the path cannot be read — an
   elevated process will not say — the pane restores as empty and explains why, rather than
   restoring something wrong.

6. **The toolbar's New menu *is* the profile list**, rebuilt whenever it opens. Adding a profile in
   Settings makes it appear there, in every empty pane and in the palette at once, because all of
   them read the same list. `ProfilePaneFactory` is the single place a profile becomes a pane.

7. **The application has an icon.** `assets/winmux.svg` is the source — a window split into panes
   with one focused terminal, which is the product — and `assets/build-icon.py` rasterises it into
   a multi-resolution `.ico` carried by every executable and shown in every window.

## Consequences

- Any application on the machine can be put in a pane in three clicks, and kept as a profile if it
  is worth keeping. Opening something once does not require curating a list first.
- `HostStrategy` on a profile defaults to `Auto`, so the shipped quirks database (ADR 0003) still
  decides for anything it knows. The window class and title fields are there for the rest, and are
  only shown when the profile opens an application.
- `LaunchProfile` overrides `Equals`/`GetHashCode` because a record compares a list member by
  reference. The settings UI diffs profiles to decide whether to write the file, so the generated
  behaviour would have silently dropped edits and rewritten files nobody changed.
- Adoption is the first half of CLAUDE.md section 9's open question. Dragging a running window into
  a pane is still not implemented; picking one from a list is.
- The catalogue is read on every picker open rather than cached. At 199 ms off the UI thread that
  is cheaper than deciding when a cache is stale, and applications do get installed.

## What failed

- **Rasterising an SVG on Windows without native dependencies is harder than the drawing.**
  `cairosvg` needs a cairo that Windows does not ship; `rlPyCairo` prefers `cairocffi`, which needs
  the same; with `pycairo` supplying one, `svglib`'s renderPM path then failed on rounded
  rectangles. Headless Chrome renders it correctly, needs nothing installed that a Windows machine
  with a browser does not already have, and is a real browser engine rather than a partial
  reimplementation. `assets/build-icon.py` says all of this so the next person does not repeat it.
- **At 16×16 the terminal prompt in the icon is mush.** The split-window shape still reads, which is
  what identifies it in a taskbar, but a hand-simplified 16px variant would be better and is not
  done.
- **Escaping defeated the tooling repeatedly.** Backslashes in csproj paths became bell characters,
  `…` became the literal `2026`, and backticks in a heredoc were executed by the shell. Every
  one produced a file that looked plausible and was wrong. Editing by exact literal replacement,
  and reading back what landed, is the only reliable way to touch these files.
