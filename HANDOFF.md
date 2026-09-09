# HANDOFF

Rolling snapshot of where the work stands. Read this before doing anything; update it before you
stop. Format and rules: `AGENTS.md`, or `CLAUDE.md` section 10.

**Last updated:** 2026-09-10

---

## Where we are

**Phase 0 (spikes).** One of four is done. No product code exists — nothing in `WinMux.Core` or
any other project yet, and the stack is still a hypothesis. Phase 1 does not start until all four
spikes have answers and ADRs.

| Spike | Status |
|---|---|
| 1 — ConPTY pane, VT rendering | **not started.** Blocks the stack decision. |
| 2 — reparent 4 app classes at mixed DPI | not started |
| 3 — hang test | **done** → [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md) |
| 4 — cwd capture | not started |

## What just happened

**2026-09-10** — repo initialized, published, and spike 3 run end to end.

- `git init` on `main`, MIT license, pushed to **https://github.com/rennerdo30/winmux** (public).
  `.gitattributes` normalizes to LF, since portability is a design discipline here.
- Built `spikes/03-hang-test/` — one binary, three roles (`shell` / `panehost` / `hangapp`),
  six scenarios × three layout modes. `run-matrix.ps1` runs the lot in ~3 minutes.
- Wrote [ADR 0001](docs/adr/0001-out-of-process-pane-hosts.md). Out-of-process hosting is
  **confirmed**, but for a narrower reason than `CLAUDE.md` assumed, and the spike found a
  second freeze mechanism the contract did not describe. `CLAUDE.md` section 5 updated to match.
- Added the handoff protocol to `CLAUDE.md` (new section 10) and created `AGENTS.md`.

The three findings that change how the product gets built:

1. **The UI thread must never make a synchronous window call against a foreign window.**
   `SetWindowPos` on a wedged app blocked the shell for the full 6 s. This hits *attach mode
   as hard as embed mode* — attach mode is a compatibility fallback, not a stability one.
2. **Never hard-kill a pane host that still owns an embedded window** — it destroys the app's
   window and leaves a windowless orphan process. Detach, then terminate.
3. **The input-queue attachment claim is still unmeasured.** See below.

## The next action

**Run spike 1 (ConPTY pane).** It blocks the stack decision, which blocks everything else:
spawn pwsh, render VT, resize correctly, confirm no input lag. The open question it must
settle is *adopt an existing VT parser/renderer or write one* — and if that looks like a
multi-week sink, that is the signal to reconsider .NET + Avalonia rather than grind through it.

Put it in `spikes/01-conpty/`, write ADR 0002 when it answers.

## Blocked / needs a human

- **Nothing is blocking right now.** Spike 1 can start immediately.
- **Session file format — JSON or TOML?** Needed before the first persistence write (Phase 2),
  not before spike 1. `CLAUDE.md` requires an ADR and says never mix the two.
- **.NET version.** `CLAUDE.md` specifies .NET 9; this machine has SDKs **10.0.400 and 8.0.423,
  no 9.x**. The spike targets `net10.0-windows` and builds clean. Worth confirming that .NET 10
  is the intended target before any product code is written, so the contract stops saying 9.

## Do not re-do

- **Do not use `GetMessageW` to bound an observation window.** It blocks. The first spike-3
  lifecycle results were wrong because of this: a "3 second" window silently became 2m52s and
  4m55s, and T6 was reported as APP LOST when detach had actually succeeded. Use `PeekMessage`
  with a stopwatch. Both scenarios were re-run and the corrected results are in ADR 0001.
- **Do not treat spike 3's T3 result as clearing the transitivity question.** T3 chained
  `shell → host → app` via `SetParent` and the shell stayed responsive — but the harness measures
  *message-loop liveness*, and input-queue attachment starves *input*. It is unmeasured, not
  safe. Chaining `SetParent` from the shell to a pane host stays forbidden until someone builds
  a harness that synthesizes real keyboard/mouse input.
- **Do not design a "control" scenario that shares the code path under suspicion.** Spike 3's T1
  was meant to be an inert baseline and froze harder than anything else, which is how the second
  freeze mechanism was found — but it meant the first matrix could not separate the two causes.
  The `--nolayout` mode had to be added afterwards.
- **`gh` is installed but not on this session's PATH** (installed via winget mid-session).
  Invoke it as `"C:\Program Files\GitHub CLI\gh.exe"`, or open a fresh terminal.
- **`E:\Development\winmux` resolves to `D:\Development\winmux`** (subst or junction). Git records
  the `D:` path. Harmless, but it explains any tooling that disagrees about the repo root.
