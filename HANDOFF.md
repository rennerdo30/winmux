# HANDOFF

Rolling snapshot of where the work stands. Read this before doing anything; update it before you
stop. Format and rules: `AGENTS.md`, or `CLAUDE.md` section 10.

**Last updated:** 2026-09-10

---

## Where we are

**Phase 0 (spikes).** Two of four done, including the one that gated the stack. Still no product
code — nothing in `WinMux.Core` or any other project. Phase 1 starts once all four have answers.

| Spike | Status |
|---|---|
| 1 — ConPTY, terminal stack | **done** → [ADR 0002](docs/adr/0002-terminal-stack.md) |
| 2 — reparent 4 app classes at mixed DPI | **not started — next** |
| 3 — hang test | **done** → [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md) |
| 4 — cwd capture | not started |

**The stack is decided: .NET 10 + Avalonia 12 + C#**, VT engine adopted rather than written.

## What just happened

**2026-09-10** — repo published, spikes 3 and 1 run end to end.

- Repo at **https://github.com/rennerdo30/winmux** (public, MIT).
- Spike 3 (`spikes/03-hang-test/`) → ADR 0001. Out-of-process pane hosts confirmed, but for a
  narrower reason than the contract assumed, plus a freeze mechanism it never described.
- Spike 1 (`spikes/01-conpty/`) → ADR 0002. Stack confirmed; the "terminal rendering may sink the
  stack" risk is retired.
- `CLAUDE.md` updated for both. Handoff protocol added (§10) and `AGENTS.md` created.

Findings that change how the product gets built:

1. **The UI thread must never make a synchronous window call against a foreign window.**
   `SetWindowPos` on a wedged app blocked the shell for a full 6 s. Hits attach mode *as hard as*
   embed mode — attach is a compatibility fallback, not a stability one.
2. **Never hard-kill a pane host that still owns an embedded window** — it destroys the app's
   window and leaves a windowless orphan process. Detach, then terminate.
3. **ConPTY's round-trip floor is ~0.08 ms**, so a whole 60 Hz frame is available for rendering.
   PowerShell's own echo latency is ~15.6 ms through the identical pty — that is PSReadLine, not
   us. Benchmark our input path against `cmd` or the shell's cost masks ours.
4. **Adopt the VT engine, behind our own `ITerminalEngine` interface.** `Terminal.Emulation`
   parses at 14–36 MiB/s (3–10× what ConPTY delivers) and is correct on reflow, alt screen,
   CJK double-width and OSC 8 — but it is 5 weeks old, single-author, and **its source repository
   does not exist publicly**. Adopt, isolate, keep the swap cheap.

## The next action

**Run spike 2 (reparent four apps).** It is the last real unknown: reparent Notepad, Explorer, a
Chromium app (VS Code or a browser) and a packaged/UWP app into a borderless host; resize, move
and detach cleanly, at mixed DPI. This is what turns "any Windows app" from a claim into a
documented compatibility surface, and it seeds the quirks database.

Reuse `spikes/03-hang-test/` — it already has working embed/detach with exact style capture and
restore (`Embedding.Embed` / `Embedding.Detach`), and an out-of-process host. Put it in
`spikes/02-reparent/`, write ADR 0003.

Spike 4 (cwd capture) can follow; it is independent.

## Blocked / needs a human

- **Nothing is blocking.** Spike 2 can start immediately.
- **Session file format — JSON or TOML?** Needed before the first persistence write (Phase 2).
  `CLAUDE.md` requires an ADR and says never mix the two.
- **Supply-chain call before v1.** `Terminal.Emulation` has no public source. Decide whether that
  is acceptable for a shipping dependency, or whether to budget for writing the engine after all.
  Not urgent — the `ITerminalEngine` seam keeps it cheap to reverse.

## Do not re-do

- **Do not hand-roll ConPTY.** It fails *silently and convincingly*: conhost starts, the title
  updates, init/teardown sequences arrive — and the child's output goes to the host's console
  instead of the pty. Cause: `CreateProcess` propagates the host's std handles, which beat the
  pseudoconsole when the host has a console. Fix is `STARTF_USESTDHANDLES` with null std handles
  (see `spikes/01-conpty/src/ConPty.cs`). Microsoft's sample never shows this because its host is
  a GUI app. Use `Porta.Pty` instead.
- **When interop fails in a way that reading it does not reveal, run a control.** Three careful
  re-reads of the ConPTY code found nothing; the same test through `Porta.Pty` located the bug in
  minutes. `spikes/01-conpty --control` exists for this.
- **Do not accumulate unbounded text in a measurement harness.** `Collector.WaitFor` re-scanned
  the whole stream per chunk — O(n²) — and the first throughput numbers measured the harness
  (2.8 MiB/s reported vs 3.7–7.8 actual).
- **Do not write a verification whose pattern can match the question.** The cmd resize check
  waited for the word `Columns`, which appears in the echo of the typed command, so it passed
  without the shell ever answering. Require the answer's shape (`Columns:\s*(\d+)`), then compare.
- **Do not use `GetMessageW` to bound an observation window.** It blocks. Spike 3's first
  lifecycle results were wrong because of this — a "3 second" window silently became 2m52s and
  4m55s, and T6 was reported as APP LOST when detach had actually succeeded. Use `PeekMessage`
  with a stopwatch.
- **Do not treat spike 3's T3 result as clearing the transitivity question.** T3 chained
  `shell → host → app` via `SetParent` and the shell stayed responsive — but the harness measures
  *message-loop liveness*, and input-queue attachment starves *input*. Unmeasured, not safe.
  Chaining `SetParent` from the shell to a pane host stays forbidden.
- **Do not design a "control" scenario that shares the code path under suspicion.** Spike 3's T1
  was meant to be inert and froze harder than anything else.
- **`dotnet package search --exact-match --format json` returns unusable rows** (one per version,
  fields empty). Query `https://azuresearch-usnc.nuget.org/query?q=packageid:<id>` directly.
- **`gh` is installed but not on this session's PATH** (winget-installed mid-session). Invoke as
  `"C:\Program Files\GitHub CLI\gh.exe"`, or open a fresh terminal.
- **`E:\Development\winmux` resolves to `D:\Development\winmux`** (subst or junction). Git records
  the `D:` path. Harmless, but it explains tooling that disagrees about the repo root.
