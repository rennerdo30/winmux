# ADR 0017 — Input-queue attachment: measured

**Date:** 2026-09-15
**Status:** Accepted
**Answers:** the open question in [ADR 0001](0001-out-of-process-pane-hosts.md) finding 4, and
CLAUDE.md section 9's last unanswered question.

## Context

CLAUDE.md section 5 has carried this since Phase 0:

> **Input queue attachment.** `SetParent` across processes attaches the two threads' input queues,
> *transitively*. One hung app hangs everyone attached to it — including the WinMux UI thread.
> **This is why pane hosting is out-of-process.** Non-negotiable.
> *Status: unverified.*

Spike 3 chained `shell → host → app`, wedged the app, and found the shell responsive — but its
harness watched the message loop tick, which is not the same as being able to receive a keystroke.
So the claim stayed "true and unproven", and a load-bearing architectural constraint ("never
`SetParent` a pane host into the shell window") rested on it for three phases.

`spikes/05-input-queue` measures it: two top-level windows in two processes, synthesized keyboard
input via `SendInput`, arrival timed in the receiving window procedure. Run twice — as unrelated
top-level windows, and with the app's window `SetParent`'d into the host's, which is the topology
the architecture forbids.

## What was measured

Ten keystrokes before wedging the app and ten after, plus the time for the host to take the
foreground while the app is wedged. Six seconds of wedge. Every run identical to the millisecond.

| | detached | attached (`SetParent`) |
|---|---|---|
| keys received, app healthy | 10/10 @ 1.9 ms | 10/10 @ 1.9 ms |
| keys received, **app wedged** | **10/10 @ 1.1 ms** | **10/10 @ 0.9 ms** |
| **taking focus while wedged** | **201 ms** | **4,386 ms** |

## Decision

**The constraint stands. The stated reason for it is wrong.**

1. **Input is not starved.** With the queues attached and the other process wedged, keystrokes
   arrived at the same latency as with no relationship at all — about one millisecond, ten out of
   ten, in every run. The "one hung app hangs everyone attached to it" reading, as applied to
   *receiving input*, is not what happens.

2. **Focus operations block for the full duration of the wedge.** Taking the foreground cost 201 ms
   detached and 4,386 ms attached — the entire remainder of the six-second wedge, reproducible to
   two milliseconds across six runs. A shell that shares an input queue with a frozen application
   freezes the moment it tries to move focus, which it does every time a pane is selected.

So the hazard is real and severe, and it belongs in the same family as the trap ADR 0001 actually
measured: **synchronous window operations against a wedged thread block the caller.** Attaching
input queues does not create a new kind of failure, it widens the set of calls that can trigger the
old one — from calls *on the foreign window* to calls about focus *anywhere in the attached set*,
including on our own windows.

## Consequences

- **`SetParent` from the shell to a pane host stays forbidden**, and the rule can now be stated
  from measurement rather than from folklore. Pane hosts stay top-level and positioned
  ([ADR 0001](0001-out-of-process-pane-hosts.md)).
- **CLAUDE.md section 5's wording is corrected.** "Starves the shell of input" was the wrong
  description and pointed future work at the wrong symptom; someone testing for dropped keystrokes
  would have measured 10/10 and concluded the trap was mythical — which is very close to what spike
  3 concluded.
- **The remaining unmeasured claim is transitivity.** This measured one attachment, shell ↔ app. The
  documented transitive case (shell ↔ host ↔ app) was not run, and the constraint makes it
  unnecessary to know.
- The harness exists and is cheap to rerun: `dotnet run --project spikes/05-input-queue -- --mode attached`.

## What went wrong on the way

The first version of the harness reported "STARVED: no input arrived while the app was wedged" —
and it also reported zero keystrokes arriving with the app perfectly healthy. Both numbers were the
harness failing to take the foreground, not Windows failing to deliver input.

`SetForegroundWindow` is refused to a process that is not already in the foreground, and `SetFocus`
only works from the thread that owns the window. Neither returns anything the original code looked
at. The fix was to borrow the current foreground thread's input queue for the length of the call —
the same mechanism this spike exists to measure — and, more importantly, to **make the harness
refuse to report a result it has not earned**: it now aborts if it cannot take the foreground, and
aborts if zero keys arrive while the app is healthy.

That is the third time in two days that a measurement harness, not the product, produced the
alarming result ([ADR 0016](0016-windows-11-chrome.md) has the other two). The rule earned here:
**every harness needs a control case that must pass, checked before the experimental number is
believed.**
