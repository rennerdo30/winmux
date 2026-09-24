# ADR 0024 — Reading a terminal while it is written to

- **Status:** accepted
- **Date:** 2026-09-24
- **Spike:** none

## Context

A crash log from another machine, WinMux 0.7.4:

```
2026-09-24 05:42:46.514 +02:00  unhandled exception on the UI thread  pid 30228  version 0.7.4
System.IndexOutOfRangeException: Index was outside the bounds of the array.
   at WinMux.Terminal.TerminalEmulationEngine.CopyRow(Int32 rowIndex, Span`1 destination)
   at WinMux.Shell.TerminalPaneControl.Render(DrawingContext context)
   at Avalonia.Rendering.Composition.CompositingRenderer.UpdateCore()
```

The process ended. It is the first crash the crash log has caught since it was added, and it caught
it completely: thread, stack, version, and a clean `process exit, code 0` on the run before, so
"crashed" and "was closed" were distinguishable without asking.

`TerminalEmulationEngine` guards every member with one lock, which made each *call* atomic and a
*frame* atomic in no way at all. `Render` read `Columns`, then `Rows`, then `TotalRows`, worked out
which absolute rows were visible, and then called `CopyRow` once per row — nine to sixty separate
acquisitions of the lock, with the pty reader thread free to write between any two of them.

Measured rather than reasoned about, on a 40×10 engine holding 100 lines of history:

| | `TotalRows` | `ScrollbackCount` |
|---|---|---|
| before `ESC[?1049h` | 101 | 91 |
| after | 10 | 0 |

**Entering the alternate screen discards the entire scrollback in a single write.** Every row index
the renderer had just computed then pointed past the end of the buffer, and `CopyRow` threw. A
full-screen program starting in a pane that held any history is enough — vim, less, htop, or Claude
Code, which 0.7.4 had just made work in a pane. Column count moves the same way through `ESC[?3h`,
against a `destination` the caller had already sized from `Columns`.

Four other call sites read rows the same way — copy, copy-all, select-word, search — so the fault
was a property of the contract, not of the renderer.

## Decision

**`CopyRow` is total.** A row index outside the buffer returns an empty row, and a row wider than
the destination is copied as far as it fits; the returned `Length` always describes what is in the
destination, so it is safe to index with. It threw `IndexOutOfRangeException` and `ArgumentException`
respectively before.

The reasoning is that neither condition is a caller error. The caller asked about a buffer that a
writer thread has since changed, which is the normal condition of a terminal, not a bug. The honest
answer to "row 4,207" when the buffer has ten rows is "that row is gone" — not a crash, and not the
nearest row either, which would draw text that was never there. One blank line is drawn for one
frame, and the repaint that the `Updated` event has already scheduled is correct.

The rejected alternative was an atomic frame API — one call, one lock, the whole visible band copied
out. It is better on paper and it removes the torn frame as well as the crash. It was rejected for
now because it fixes one of the five call sites and leaves the other four to be redesigned one at a
time, while the change above fixes the class. Revisit it if tearing ever becomes visible, which at
60 Hz it should not.

**`TerminalViewport.OnBufferGrew` becomes `OnBufferChanged`** and clamps on a shrink as well as
growing on a growth. The same write that caused the crash also left the view parked above history
that no longer existed, where `IsFollowing` is false and the renderer draws no cursor: start vim
after scrolling back, get a terminal with no cursor in it.

## Consequences

- A genuinely wrong row index now renders blank instead of crashing. That is the cost of the
  decision and it is accepted: CLAUDE.md priority 2 says a misbehaving pane must never take down the
  shell, and priority 1 says an unsaved session must not be lost — a crash costs both.
- The lock is still taken per row. Nothing here makes rendering faster.
- `ConcurrentReadTests` pins the mechanism in three deterministic tests and the race in a fourth.
  All four were verified against the bug by putting it back: each went red, the race one within two
  seconds.

## Notes

The crash log earned its place here. It was added after WinMux vanished during a test with nothing
in the Windows event log and nothing of its own to say why; the first real crash it saw arrived
fully diagnosed, from a machine nobody could attach a debugger to. It is undocumented, though — the
troubleshooting page does not mention it, so the user had to ask where the logs were. Fixed in the
same change.
