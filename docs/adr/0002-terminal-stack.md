# ADR 0002 — Terminal stack: .NET 10 + Avalonia, adopted VT engine behind our own interface

- **Status:** accepted
- **Date:** 2026-09-10
- **Spike:** [`spikes/01-conpty`](../../spikes/01-conpty) (Phase 0, spike 1)
- **Settles:** CLAUDE.md §9 "Terminal rendering: adopt or write? **Blocks the stack decision.**"

## Context

CLAUDE.md §2 proposed .NET 9 + Avalonia and named its own main risk: *"terminal rendering. .NET
has no terminal control of Windows-Terminal quality. Either adopt an existing VT parser/renderer
or write one — Phase 0 must settle which. If this proves to be a multi-week sink, that is the
signal to reconsider the stack."*

Spike 1 ran in two stages: (A) what ConPTY itself costs, hand-rolled, with no library in the
picture; (B) whether an adoptable VT engine exists, measured headlessly.

## Findings

### Stage A — ConPTY (measured on Windows 10.0.26200, .NET 10.0.11, x64)

| | pwsh 7.6.5 | powershell 5.1 | cmd |
|---|---|---|---|
| first byte | 27–56 ms | 27–45 ms | 25–34 ms |
| prompt ready | ~700 ms | ~630 ms | ~455 ms |
| **echo latency p50** | **15.6 ms** | **15.6 ms** | **0.07–0.09 ms** |
| echo latency p95 | 16.2 ms | 16.3 ms | 0.10–0.16 ms |
| bulk throughput | 3.7–5.2 MiB/s | 6.1–7.8 MiB/s | 1.5–1.8 MiB/s |
| resize (3 sizes) | verified | verified | verified |

**The transport is not the bottleneck.** cmd echoes a keystroke in **0.08 ms** through the exact
same pseudoconsole that takes PowerShell 15.6 ms. The ~15.6 ms is therefore PSReadLine's own
render cadence, not ConPTY's and not ours — WinMux cannot fix it and should not be blamed for it.
The pty round trip leaves essentially a whole 60 Hz frame of budget for rendering.

`ResizePseudoConsole` was verified end to end: after each resize the shell was asked its own
dimensions and agreed, at 100×30, 160×50 and 80×24, for all three shells.

### Stage B — VT engine (`Terminal.Emulation` 0.3.3, headless)

| test | result |
|---|---|
| raw parse throughput | **14 MiB/s** (no scrollback) / **36 MiB/s** (10k-line scrollback) |
| end-to-end pwsh → ConPTY → engine | grid correct, title captured, marker present |
| resize with reflow, 40 → 20 → 100 cols | text preserved both directions |
| alternate screen enter/leave | correct, primary content restored |
| CJK double-width (`ab你好cd`) | cell widths `nnW-W-nn` — correct |
| OSC 8 hyperlinks | captured and resolvable |

**Parsing is free relative to the transport** — 14–36 MiB/s against ConPTY's 4–8 MiB/s, so
3–10× headroom. The engine is a VT500 state machine with a response channel (DA, DSR/CPR,
DECRQM), truecolor, grapheme-aware widths, scrollback and reflow. It exposes cells as a plain
`Cell[]` field per line and a `Version` counter for damage tracking, so a renderer can read the
grid without allocating.

Critically it ships **separately from the Avalonia control** (`Terminal.Emulation` has no UI
dependency and no external dependencies), which is what made it testable headlessly and what
makes it swappable.

### The maturity problem

`Terminal.Emulation` / `Terminal.Pty` / `Terminal.Avalonia` are MIT, first published
**2026-08-06**, latest **2026-09-09**, 8 versions in ~5 weeks, ~880 downloads, single author.
The repository URL declared in the package metadata,
`https://github.com/b-y-t-e/Terminal.Avalonia`, **does not exist** — 404 via authenticated
GitHub API; the owner account has 37 public repos and this is not among them.

So: it works, it is fast, it is correct on every case tested — and it is a young, single-author,
**source-unavailable** binary dependency. Those are independent facts and the decision has to
hold both.

## Decision

1. **Stack confirmed: .NET 10 + Avalonia, C#.** The risk CLAUDE.md flagged as stack-deciding is
   resolved — a working VT engine exists and rendering has a full frame of budget. Avalonia
   12.1.2 is stable (2026-09-02). **CLAUDE.md's ".NET 9" is superseded by .NET 10**, which is
   what is installed and what these packages target.

2. **Adopt a VT engine; do not write one.** Writing a VT500 state machine with reflow, wide
   characters and alt-screen is the multi-week sink CLAUDE.md warned about, and it is now
   avoidable.

3. **Adopt it behind our own interface.** `Terminal.Emulation` types must not appear in
   `WinMux.Core` or any public signature. A narrow `ITerminalEngine` (write bytes, read cell
   grid, resize, cursor, title, response callback) sits in front of it. This is the price of
   depending on a 5-week-old package with no public source, and it is cheap: the engine's own
   surface is already close to that shape.

4. **Adopt a pty library; do not hand-roll ConPTY.** See "What failed" — hand-rolled ConPTY has
   a silent failure mode that cost this spike most of its time. `Terminal.Pty` (same author) or
   `Porta.Pty` (139k downloads, cross-platform, independent) both work. Prefer **`Porta.Pty`**
   for `WinMux.Pty`: it is independently maintained, far more widely used, and already carries
   Linux/macOS backends, which serves priority 6 at no cost.

5. **Keep a fallback documented.** If the engine is abandoned, the options are: vendor it (MIT
   permits it, but no source is published, so this means decompiled IL), or swap in another
   Avalonia terminal — `tomlm/Iciclecreek.Avalonia.Terminal`, `IvanJosipovic/SvcSystems.UI.Terminal`
   and `VitalElement/AvalonStudio.TerminalEmulator` all exist with public source. The
   `ITerminalEngine` seam is what makes that a swap rather than a rewrite.

## Consequences

- Phase 1 can start on terminal panes immediately; the hardest unknown is retired.
- We carry a supply-chain risk that must be reviewed before v1: pin exact versions, and
  re-evaluate if the package goes quiet for a quarter.
- Rendering performance is now our problem alone, and the target is clear: stay under ~16 ms per
  frame while the engine feeds us at up to 36 MiB/s. Damage tracking via `Version` matters.
- The ~15.6 ms PowerShell echo latency is a fact of the shell. Do not chase it; measure against
  cmd (0.08 ms) when benchmarking our own input path, or the shell's cost will mask ours.

## What failed

- **Hand-rolled ConPTY failed silently and cost most of the spike.** The child attached to the
  pseudoconsole — conhost even emitted the title `cmd.exe` — but its output went to the *host's*
  console instead of the pty, and only 86 bytes of conhost's own init/teardown ever came back.
  Cause: `CreateProcess` propagates the host process's standard handles to the child, and when
  the host is a console app those console handles win over the pseudoconsole. Fix:
  `STARTF_USESTDHANDLES` with all three std handles set to null. Microsoft's ConPTY sample never
  exhibits this because its host is a GUI app whose std handles are already null. Reading the
  interop three times did not find it; a control experiment (the same test through `Porta.Pty`,
  which worked immediately) found it in minutes. `Terminal.Avalonia`'s own README documents the
  same trap independently — *"under a host whose stdout is redirected to a pipe, a console child
  can inherit that pipe instead of the pseudo console."*
- **The first throughput numbers measured the harness, not ConPTY.** `Collector.WaitFor`
  materialised the entire accumulated stream and re-scanned it on every chunk — O(n²) over
  4.3 MiB. Reported 2.8–2.9 MiB/s; after replacing it with a bounded tail plus absolute
  character positions, 3.7–7.8 MiB/s. Any harness that accumulates unbounded text is measuring
  itself.
- **The cmd resize check was a false PASS.** It waited for the word `Columns`, which appears in
  the *echo* of the typed `mode con | findstr /C:"Columns"` command, so it passed without the
  shell ever answering. Caught because the printed "evidence" was the echoed command line rather
  than a number. Fixed to require `Columns:\s*(\d+)` and compare the value. A verification whose
  pattern can match the question instead of the answer is not a verification.
- **`dotnet package search --exact-match --format json` returned unusable rows** (one row per
  version, all fields empty). The NuGet search API was queried directly instead.
