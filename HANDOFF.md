# HANDOFF

Rolling snapshot of where the work stands. Read this before doing anything; update it before you
stop. Format and rules: `AGENTS.md`, or `CLAUDE.md` section 10.

**Last updated:** 2026-09-10

---

## Where we are

**Phase 0 is COMPLETE.** All four spikes ran, all four ADRs are written, and the gate is open.
There is still **no product code** — nothing in `WinMux.Core` or any other project. Everything
under `spikes/` is throwaway except two artefacts that ship:
`spikes/02-reparent/quirks-seed.json` and `spikes/04-cwd/profiles/`.

| Spike | Result |
|---|---|
| 1 — ConPTY, terminal stack | [ADR 0002](docs/adr/0002-terminal-stack.md) — stack confirmed, VT engine adopted |
| 2 — reparent apps | [ADR 0003](docs/adr/0003-foreign-app-compatibility.md) — compatibility surface measured |
| 3 — hang test | [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md) — OOP hosts confirmed |
| 4 — cwd capture | [ADR 0004](docs/adr/0004-cwd-capture.md) — layered strategy 85%, snippets mandatory |

**Stack: .NET 10 + Avalonia 12 + C#.** VT engine adopted (`Terminal.Emulation`), pty adopted
(`Porta.Pty`), both behind our own interfaces.

## What just happened

**2026-09-10** — repo published at **https://github.com/rennerdo30/winmux** (public, MIT), and all
of Phase 0 executed. `CLAUDE.md` updated after each spike; handoff protocol added (§10) and
`AGENTS.md` created.

The findings that now constrain the design:

1. **The UI thread must never make a synchronous window call against a foreign window.**
   `SetWindowPos` on a wedged app froze the shell for a full 6 s, and hits *attach mode as hard as
   embed mode*. Attach is a compatibility fallback, not a stability one.
2. **Never hard-kill a pane host that still owns an embedded window** — it destroys the app's
   window and orphans the process. Detach, then terminate.
3. **ConPTY's round-trip floor is ~0.08 ms**, so a full 60 Hz frame is free for rendering.
   PowerShell's ~15.6 ms echo latency is PSReadLine's own — benchmark against `cmd`.
4. **`Terminal.Emulation` parses at 14–36 MiB/s** (3–10× what ConPTY delivers) and is correct on
   reflow, alt screen, CJK width and OSC 8 — but it is 5 weeks old, single-author, and **its
   source repository is not public**. Hence the `ITerminalEngine` seam.
5. **Ordinary apps embed and detach byte-exactly**, including Explorer and VS Code. Task Manager
   refuses with `ERROR_ACCESS_DENIED` (UIPI), UWP with `ERROR_INVALID_PARAMETER`. Both fall back
   to attach.
6. **PowerShell's PEB is permanently stale and WSL's is meaningless.** The cwd profile snippets
   are mandatory, not optional — without them a PowerShell pane restores to the wrong directory.

## The next action

**Start Phase 1: create the solution skeleton and the layout engine.**

`CLAUDE.md` §3 fixes the projects; §8 requires the layout engine to have real unit tests and
`WinMux.Core` to be platform-free **enforced by a test**, not by discipline. So the first commit
of real code should be:

1. `WinMux.sln` with `WinMux.Core` (net10.0, **no** platform references) and `WinMux.Tests`.
2. The layout tree from §4 — `Split` / `Stack` / `Leaf`, ratios, focus movement — as pure logic.
3. Unit tests for splits, ratios, focus movement and serialization round-trips.
4. The dependency-set test asserting `WinMux.Core` references no platform assembly.

Do **not** start the Avalonia shell first; the tree is what everything else rests on, and it is
the one part that can be fully tested without a window.

Before the first persistence write, settle the session file format (below).

## Blocked / needs a human

- **Session file format — JSON or TOML?** Needed before Phase 2's first write. `CLAUDE.md`
  requires an ADR and says never mix the two. Not blocking Phase 1.
- **Mixed-DPI multi-monitor is untested and needs you.** Both monitors are 144 DPI (150%), so the
  scenario `CLAUDE.md` calls "where the bugs live" never ran. Set one display to a different scale
  factor, then re-run `spikes/02-reparent/bin/Release/net10.0-windows/ReparentSpike.exe` and drag
  a hosted app between monitors. Everything else in spike 2 passed.
- **Supply-chain call before v1.** `Terminal.Emulation` has no public source. Decide whether that
  is acceptable for a shipping dependency. The `ITerminalEngine` seam keeps it cheap to reverse.
- **Does input-queue attachment actually starve the shell of input?** Spike 3 could not see it.
  Until someone builds a harness that synthesizes real input, chaining `SetParent` from the shell
  to a pane host stays forbidden.

## Do not re-do

**Measurement discipline** — every one of these produced a confident, wrong result first:

- **Silence is not completion.** `WaitQuiet` returned while `ping` was still running (a sleeping
  child emits nothing), so a whole scenario never executed and scored MISS everywhere. The same
  wait raced PSReadLine's predictive autosuggestion and made a non-ASCII path look like an OSC
  failure. Sync on a random marker echoed back **twice** — once as the echoed command line, once
  as the shell's output. One occurrence only proves the keystrokes arrived.
- **Do not use `GetMessageW` to bound an observation window.** It blocks; a "3 second" window
  silently became 2m52s, and spike 3's first lifecycle verdicts were wrong. Use `PeekMessage`.
- **Do not write a verification whose pattern can match the question.** A resize check waited for
  the word `Columns`, which appears in the echo of the typed command, so it passed without the
  shell ever answering.
- **If a test cannot create the condition it claims to test, delete it.** Two DPI tests scored OK
  while testing nothing: `__COMPAT_LAYER=DPIUNAWARE` does not override an app's manifest, and a
  manifest-declared awareness cannot be overridden at runtime either.
- **Do not use a loose "any new window" match** — it grabbed a stray Chrome window titled
  "Task Manager - Google Chrome" and scored the UIPI test OK. Match on process image name.
- **Do not accumulate unbounded text in a harness.** An O(n²) collector made the first throughput
  numbers measure the harness rather than ConPTY.
- **Separate the comparison form from the display form.** Folding path separators inside the same
  function printed Linux paths as `\tmp\foo` in the report.
- **When interop fails in a way that reading it does not reveal, run a control.** Three re-reads
  of the ConPTY code found nothing; the same test through `Porta.Pty` found the bug in minutes.
  Likewise `--probe` in spike 2 found the Notepad shim problem in one run.

**Platform traps:**

- **Do not hand-roll ConPTY.** It fails silently and convincingly: conhost starts, the title
  updates, init/teardown arrive — while the child's output goes to the host's console, because
  `CreateProcess` propagates the host's std handles and those beat the pseudoconsole. Fix is
  `STARTF_USESTDHANDLES` with null std handles (`spikes/01-conpty/src/ConPty.cs`). Microsoft's
  sample never shows it because its host is a GUI app. Prefer `Porta.Pty`.
- **`GetParent` returns the OWNER for a `WS_POPUP` window.** It produced a false "embed failed"
  for Calculator. Use `GetAncestor(hwnd, GA_PARENT)`.
- **Capture `GetLastError` on the line after `SetParent`.** Three intervening calls turned
  "UIPI denied (5)" and "window refuses (87)" into a useless "last error 0".
- **Match windows on process image name, never the launched pid** — Win11's `notepad.exe` is a
  shim that runs the real app as a different process.
- **Do not attempt a PEB read for a WSL pane.** It returns a confidently wrong Windows path.
- **Do not treat spike 3's T3 result as clearing the transitivity question.** The harness measures
  message-loop liveness; input-queue attachment starves *input*. Unmeasured, not safe.

**Environment:**

- **`gh` is installed but not on this session's PATH** (winget-installed mid-session). Invoke as
  `"C:\Program Files\GitHub CLI\gh.exe"`, or open a fresh terminal.
- **`E:\Development\winmux` resolves to `D:\Development\winmux`** (subst or junction). Git records
  the `D:` path.
- **`dotnet package search --exact-match --format json` returns unusable rows.** Query
  `https://azuresearch-usnc.nuget.org/query?q=packageid:<id>` directly.
