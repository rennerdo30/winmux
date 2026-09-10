# ADR 0005 — Layout engine: canonical mutable tree, geometric focus, format-neutral snapshots

- **Status:** accepted
- **Date:** 2026-09-10
- **Code:** `WinMux.Core/Layout`, `WinMux.Core/Session`, `WinMux.Tests`

## Context

Phase 1 starts with the layout tree because everything else rests on it and it is the one part
fully testable without a window. CLAUDE.md §4 fixes the shape (Split / Stack / Leaf), §3 requires
`WinMux.Core` to be platform-free, and §8 requires real unit tests plus a dependency test rather
than discipline. This ADR records the decisions taken while building it that the contract did not
already settle.

## Decisions

**1. `Columns` / `Rows`, never `Horizontal` / `Vertical`.** Every multiplexer disagrees about what
"horizontal split" means — tmux's `split -h` produces a *vertical* divider. Naming what the
children do rather than what the divider looks like removes the ambiguity at the type level, and
the CLI and keymap will inherit the same vocabulary.

**2. The tree is mutable but canonical.** After any operation there is never a split or a stack
with fewer than two children; closing a pane collapses its container and hoists the sibling.
Canonicalising eagerly is what makes structural comparison and round-tripping honest — the
alternative is a tree that compares unequal to itself after a close-and-reopen, and a session file
full of one-child containers that a hand-editor would reasonably delete.

Invariants are enforced in the node constructors and mutators, not assumed: at least two children,
one ratio per child, every ratio ≥ `MinRatio` (0.02), ratios summing to 1, stack `ActiveIndex`
always in range.

**3. Splitting again in the same direction extends the existing split rather than nesting.** "Split
the same way again" means widening the row, which is what users read on screen. Nesting would make
subsequent ratio arithmetic behave in a way that looks like a bug.

**4. Focus movement is geometric, not structural.** `MoveFocus(Left|Right|Up|Down)` arranges the
tree and picks the nearest visible pane in that direction that overlaps on the perpendicular axis,
tie-breaking on centre alignment. The tree's shape is not what the user sees, so a structural walk
gives wrong answers across nesting boundaries — in a 2×2 grid the pane below the top-left one lives
in a different subtree. This means directional focus needs `Bounds`, and is a no-op before the
shell has supplied them.

**5. A pane in an inactive tab has no rectangle at all**, rather than a hidden or zero one.
`Arrangement.PaneRects` contains visible panes only. Asking for the geometry of something that is
not on screen is a bug worth surfacing, and it keeps focus movement from targeting buried panes.

**6. Pixel distribution is cumulative, not per-child.** Rounding each child independently loses or
gains pixels as the child count grows, and the drift is visible as a divider that wobbles while
being dragged. The last child takes the remainder so the children plus dividers always sum exactly
to the parent extent.

**7. Session snapshots are format-neutral, and a node is one record with optional fields.** The
JSON-vs-TOML question (§9) is still open and must not leak into the model. A single `NodeSnapshot`
with a `Kind` discriminator, rather than a polymorphic hierarchy, because the session file has to
stay hand-editable — discriminated unions serialize badly into both formats and read worse.

**8. Working directories carry provenance.** `WorkingDirectory` is `(Path, CwdSource, CapturedAt)`,
implementing ADR 0004 decision 5. `CwdSource` is ordered worst-to-best so choosing the better of
two captures is a comparison, and `WorkingDirectory.Better` prefers the more trustworthy source,
breaking ties on freshness.

**9. Restoring validates rather than repairs.** A malformed session throws `SessionFormatException`
with a message saying what to do — a future file version says "upgrade WinMux" rather than being
discarded. §8 forbids silent failure around persistence, and a session file is user data.

## Consequences

- The engine is 5 source files and has no dependencies whatsoever, which the tests enforce.
- Directional focus requires the shell to keep `Bounds` current on resize. That coupling is
  deliberate and cheap; the alternative is a structural approximation that is subtly wrong.
- Canonicalisation means a tab group of one collapses back to a plain pane. If a future design
  wants a persistent single-tab group, that is a model change and needs its own ADR.
- Nothing here writes a file yet. Persistence, debouncing and timestamped snapshots are Phase 2,
  gated on the format decision.

## What failed

- **The first pass of `CoreIsPlatformFreeTests` could not catch the thing it exists to catch.**
  `Assembly.GetReferencedAssemblies()` reports only assemblies the compiler actually emitted a
  reference for, so a platform package that is referenced but not yet *called* passes every
  assertion while the boundary is already gone. Verified by adding `System.Drawing.Common` to
  `WinMux.Core`: the suite stayed green. Fixed by also asserting on the declared dependencies in
  the project file itself, passed to the test as assembly metadata — that mutation now fails.
- **The TFM assertion turned out to be unreachable.** Switching `WinMux.Core` to `net10.0-windows`
  fails the *build* (the test project targets `net10.0` and cannot reference it), so the test never
  runs. That is a stronger guard than the test, but it means the assertion is belt to the build's
  braces, not the primary defence — worth knowing before someone "simplifies" the test away.
- **Both mutations had to be run to learn this.** All 68 tests passed on the first attempt, which
  is precisely when a suite deserves to be distrusted. Naive per-child rounding was also injected
  to confirm the pixel test fails (it does, at 7 and 11 panes); a test that cannot fail is not
  evidence.
