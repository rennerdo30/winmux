# ADR 0001 — Out-of-process pane hosts, and the rule that actually keeps the shell alive

- **Status:** accepted
- **Date:** 2026-09-10
- **Spike:** [`spikes/03-hang-test`](../../spikes/03-hang-test) (Phase 0, spike 3)

## Context

CLAUDE.md section 5 asserts that pane hosting must be out-of-process because `SetParent`
across processes attaches the two threads' input queues transitively, so one wedged app
would hang the WinMux UI thread. Priority 2 (stability of the shell) rests on this, and
Phase 3 is explicitly forbidden from "simplifying" it away.

The claim had never been measured. Spike 3 measured it.

### Method

Three processes — `shell`, `panehost`, `hangapp` — from one binary. `hangapp` is a plain
top-level Win32 window that stops pumping messages by sleeping 6s on its UI thread on
command. The shell samples its own responsiveness from two vantage points:

- **its UI thread**, via a 50 ms timer, recording inter-tick gaps and the wall time of the
  per-tick pane `SetWindowPos` (the layout call a real shell makes constantly);
- **a background watchdog thread**, which owns no windows and is therefore never part of
  any attached input queue, calling `IsHungAppWindow` and `SendMessageTimeout(WM_NULL,
  SMTO_ABORTIFHUNG, 250ms)` against the shell's own window.

Four topologies, each run in three layout modes:

| | topology |
|---|---|
| **T1** | attach-mode baseline — app stays top-level, no `SetParent` anywhere |
| **T2** | in-process embed — the *shell* calls `SetParent(app, shellHwnd)` |
| **T3** | OOP host, chained — host reparents app; *shell reparents the host* |
| **T4** | OOP host, positioned — host reparents app; shell only repositions the host's top-level window |

Layout modes: `sync` (plain `SetWindowPos` from the UI thread), `async`
(`SWP_ASYNCWINDOWPOS`), `nolayout` (shell never touches the pane window — isolates
input-queue attachment from blocking window calls).

## Findings

    Scenario            sync          async        nolayout
    T1 attach       FROZEN 6014ms   RESPONSIVE    RESPONSIVE
    T2 in-proc      FROZEN 5965ms   RESPONSIVE    RESPONSIVE
    T3 OOP chained  RESPONSIVE      RESPONSIVE    RESPONSIVE
    T4 OOP posed    RESPONSIVE      RESPONSIVE    RESPONSIVE

    T5 host killed hard      -> app window DESTROYED, process orphaned (APP LOST)
    T6 host detaches + quits -> app restored top-level, style byte-exact (APP SURVIVED)

**1. The freeze that reproduces is not the one the contract predicted.** In T1 and T2 the
shell's UI thread blocked for the full 6 s *inside `SetWindowPos`* (`maxLayoutCall=6011ms`,
`maxUiGap=6014ms`). `SetWindowPos` sends `WM_WINDOWPOSCHANGING`/`CHANGED` synchronously to
the target window's thread; that thread is asleep, so the caller blocks. The watchdog
confirmed a genuinely hung shell (`IsHungAppWindow` true, `SMTO_ABORTIFHUNG` failing).

**2. This mechanism hits attach mode exactly as hard as embed mode.** T1 performs no
`SetParent` at all and froze identically to T2. CLAUDE.md presents attach mode as the safer,
more compatible fallback; against a wedged app it is not safer at all. This mechanism is not
described anywhere in the contract.

**3. Out-of-process hosting did protect the shell (T3, T4).** The reason is narrower than
the contract states: the shell only ever calls window APIs on the *host's* window, and the
host process keeps pumping even while the app inside it is wedged. The isolation comes from
never touching the foreign window, not from anything about input queues.

**4. The transitivity claim is UNRESOLVED, not confirmed and not refuted.** T3 deliberately
chained `shell -> host -> app` via `SetParent`, which should attach all three input queues.
The shell stayed responsive in all three layout modes. But the harness measures *message-loop
liveness*, and input-queue attachment's documented symptom is *input starvation* — keyboard
and mouse events not being delivered. The spike never synthesized input, so it cannot see
that. Treat T3's green result as "not measured", not as permission to chain `SetParent`.

**5. A hard host death destroys the hosted app's window (T5).** Terminating the host leaves
the app *process* alive with no window — unrecoverable from the user's point of view, and
exactly the "lost application" outcome section 5 says users will not forgive. Graceful
detach (T6) restores parent, style, ex-style and rect exactly.

## Decision

1. **Keep out-of-process pane hosts.** Confirmed to protect the shell, for a different
   reason than assumed. Section 5's conclusion stands; its stated rationale is corrected here.

2. **The shell's UI thread must never make a synchronous window call against a foreign
   window.** This is the operative rule and it outranks the embed/attach distinction. All
   pane geometry goes through a dedicated layout thread, or uses `SWP_ASYNCWINDOWPOS`.
   `SWP_ASYNCWINDOWPOS` fully mitigated the freeze in T1/T2, but it only covers
   `SetWindowPos`; every other cross-process call (`SendMessage`, `SetFocus`, `DestroyWindow`,
   `SetParent` itself) has the same hazard and no async flag.

3. **The shell must not reparent pane-host windows into its own window.** Pane hosts stay
   top-level and are positioned to follow the pane rect (T4). Until the input-starvation
   question is settled, chaining `SetParent` is forbidden.

4. **Never hard-kill a pane host that still owns an embedded window.** Detach first, then
   terminate. A panic path that skips detach loses the application.

## Consequences

- Attach mode is demoted: it is a *compatibility* fallback, not a *stability* one. The README
  and CLAUDE.md both imply otherwise and have been corrected.
- Pane geometry becomes asynchronous, which makes resize visually laggier than a naive
  implementation and means the shell cannot assume a pane reached its target rect. Layout
  code must tolerate a pane whose actual rect trails the model.
- Killing a wedged pane needs a detach-then-kill sequence with its own timeout, since detach
  itself calls `SetParent` on a possibly-wedged window and can block. That sequence belongs
  on the layout thread, never the UI thread.

## What failed

- **The first T5/T6 verdicts were wrong and were discarded.** The lifecycle path pumped with
  `GetMessageW` while setting no timer, so it blocked instead of observing; the "3 second"
  window silently stretched to 2m52s (T5) and 4m55s (T6), and the snapshot recorded state
  long after unrelated cleanup had killed the processes. T6 was reported as APP LOST when the
  host log showed detach had succeeded correctly. Fixed by bounding the pump with
  `PeekMessage`; both scenarios were re-run.
- **T1 was designed as an inert baseline and was not inert.** It froze, which is what exposed
  finding 1. A "control" that shares the suspect code path is not a control — the `nolayout`
  mode was added afterwards to get a real one.
- **`IsHungAppWindow` + `SendMessageTimeout` are insufficient to test the input-queue claim.**
  Both probe message-loop liveness. Choosing them meant the headline assertion of section 5
  went unmeasured; this was only noticed when T3 came back green.
