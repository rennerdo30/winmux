# HANDOFF

Rolling snapshot of where the work stands. Read this before doing anything; update it before you
stop. Format and rules: `AGENTS.md`, or `CLAUDE.md` section 10.

**Last updated:** 2026-09-10

---

## Where we are

**Phase 0 (spikes).** Three of four done. Still no product code — nothing in `WinMux.Core` or any
other project. Phase 1 starts once spike 4 has an answer.

| Spike | Status |
|---|---|
| 1 — ConPTY, terminal stack | **done** → [ADR 0002](docs/adr/0002-terminal-stack.md) |
| 2 — reparent apps at mixed DPI | **done** → [ADR 0003](docs/adr/0003-foreign-app-compatibility.md) |
| 3 — hang test | **done** → [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md) |
| 4 — cwd capture | **not started — next** |

**Stack decided: .NET 10 + Avalonia 12 + C#**, VT engine adopted rather than written.

## What just happened

**2026-09-10** — repo published, spikes 3, 1 and 2 run end to end.

- Repo at **https://github.com/rennerdo30/winmux** (public, MIT).
- Spike 3 → ADR 0001: out-of-process pane hosts confirmed, but for a narrower reason than the
  contract assumed, plus a freeze mechanism it never described.
- Spike 1 → ADR 0002: stack confirmed; the "terminal rendering may sink the stack" risk retired.
- Spike 2 → ADR 0003: app-compatibility surface measured, quirks database seeded
  ([`quirks-seed.json`](spikes/02-reparent/quirks-seed.json)).
- `CLAUDE.md` updated for all three. Handoff protocol added (§10), `AGENTS.md` created.

Findings that change how the product gets built:

1. **The UI thread must never make a synchronous window call against a foreign window.**
   `SetWindowPos` on a wedged app blocked the shell for a full 6 s, and hits *attach mode as hard
   as embed mode*. Attach is a compatibility fallback, not a stability one.
2. **Never hard-kill a pane host that still owns an embedded window** — it destroys the app's
   window and orphans the process. Detach, then terminate.
3. **ConPTY's round-trip floor is ~0.08 ms**, so a whole 60 Hz frame is free for rendering.
   PowerShell's ~15.6 ms echo latency is PSReadLine, not us. Benchmark against `cmd`.
4. **Adopt the VT engine behind our own `ITerminalEngine` interface.** `Terminal.Emulation` is fast
   and correct but 5 weeks old, single-author, and **its source repository is not public**.
5. **Ordinary apps embed cleanly** — Explorer and VS Code detach byte-exactly. **Task Manager
   refuses with error 5 (UIPI), UWP with error 87.** Both detectable, both fall back to attach.
6. **Match windows on process image name + class, never on the launched pid** — Win11's
   `notepad.exe` is a shim that runs the real app as a different process.

## The next action

**Run spike 4 (cwd capture)** — the last one, and it validates the headline feature. Get the
working directory out of PowerShell, cmd and WSL panes by all three strategies in `CLAUDE.md` §4
(OSC 9;9 / OSC 7 shell reporting; PEB query of the deepest child; launch-cwd fallback) and
**measure how often each succeeds**, including with a child process running in the shell.

Put it in `spikes/04-cwd/`, write ADR 0004. Reuse `spikes/01-conpty/src/ConPty.cs` for the pty
(the `STARTF_USESTDHANDLES` fix is already in it), or `Porta.Pty`.

After that, Phase 0 is complete and Phase 1 (terminal multiplexer) can start.

## Blocked / needs a human

- **Mixed-DPI multi-monitor is untested and needs you.** Both monitors are 144 DPI (150%), so the
  scenario `CLAUDE.md` calls "where the bugs live" was never exercised. Set one display to a
  different scale factor (Settings → Display → Scale), then re-run
  `spikes/02-reparent/bin/Release/net10.0-windows/ReparentSpike.exe` and drag a hosted app between
  monitors. Everything else in spike 2 passed.
- **Session file format — JSON or TOML?** Needed before the first persistence write (Phase 2).
  An ADR is required and the two must never be mixed.
- **Supply-chain call before v1.** `Terminal.Emulation` has no public source. Decide whether that
  is acceptable for a shipping dependency. Not urgent — the `ITerminalEngine` seam keeps it cheap
  to reverse.

## Do not re-do

- **Do not hand-roll ConPTY.** It fails *silently and convincingly*: conhost starts, the title
  updates, init/teardown arrive — and the child's output goes to the host's console instead of the
  pty, because `CreateProcess` propagates the host's std handles and those beat the pseudoconsole.
  Fix is `STARTF_USESTDHANDLES` with null std handles (`spikes/01-conpty/src/ConPty.cs`).
  Microsoft's sample never shows it because its host is a GUI app. Prefer `Porta.Pty`.
- **When interop fails in a way that reading it does not reveal, run a control.** Three re-reads of
  the ConPTY code found nothing; the same test through `Porta.Pty` found the bug in minutes.
  Equally, `--probe` in spike 2 found the Notepad shim problem in one run after 25 s timeouts had
  looked like a platform limitation.
- **`GetParent` returns the OWNER for a `WS_POPUP` window.** It reported a false "embed failed" for
  Calculator. Use `GetAncestor(hwnd, GA_PARENT)`.
- **Capture `GetLastError` on the line after `SetParent`.** Three intervening Win32 calls clobbered
  it and turned "UIPI denied (5)" and "window refuses (87)" into a useless "last error 0".
- **A manifest-declared DPI awareness cannot be overridden at runtime**, and `__COMPAT_LAYER=`
  `DPIUNAWARE` does not override an app's own manifest either. Two DPI tests scored OK while
  testing nothing before this was noticed. If a test cannot create the condition it claims to
  test, delete the test — do not leave a green row.
- **Do not accumulate unbounded text in a measurement harness.** `Collector.WaitFor` re-scanned the
  whole stream per chunk — O(n²) — so the first throughput numbers measured the harness.
- **Do not write a verification whose pattern can match the question.** The cmd resize check waited
  for the word `Columns`, which appears in the echo of the typed command, so it passed without the
  shell ever answering.
- **Do not use `GetMessageW` to bound an observation window.** It blocks; a "3 second" window
  silently became 2m52s, and spike 3's first lifecycle verdicts were wrong. Use `PeekMessage`.
- **Do not treat spike 3's T3 result as clearing the transitivity question.** The harness measures
  message-loop liveness; input-queue attachment starves *input*. Unmeasured, not safe. Chaining
  `SetParent` from the shell to a pane host stays forbidden.
- **Do not use a loose "any new window" match.** It grabbed a stray Chrome window titled
  "Task Manager - Google Chrome" and scored the UIPI test OK. Match on process image name.
- **`dotnet package search --exact-match --format json` returns unusable rows.** Query
  `https://azuresearch-usnc.nuget.org/query?q=packageid:<id>` directly.
- **`gh` is installed but not on this session's PATH** (winget-installed mid-session). Invoke as
  `"C:\Program Files\GitHub CLI\gh.exe"`, or open a fresh terminal.
- **`E:\Development\winmux` resolves to `D:\Development\winmux`** (subst or junction). Git records
  the `D:` path.
