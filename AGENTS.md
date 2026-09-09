# AGENTS.md — WinMux

Instructions for any coding agent working in this repository. Tool-agnostic; Claude Code reads
`CLAUDE.md`, which carries the same handoff rule.

## Read these first, in order

1. **`README.md`** — the product idea and its honest limits.
2. **`CLAUDE.md`** — the architecture contract. Priority ordering, repo layout, the embedding
   traps, the Phase 0 gate. This is the file that decides arguments.
3. **`HANDOFF.md`** — where the work actually stands right now.
4. **`docs/adr/`** — decisions already made, and what failed on the way to making them.

Do not start work before reading 1–3. `CLAUDE.md` section 1 defines a strict priority ordering;
when two goals conflict, the higher one wins, and that ordering is the product.

## HANDOFF.md is not optional

`HANDOFF.md` at the repo root is how a session that has never seen this conversation finds out
where the last one stopped. It is the difference between continuing work and restarting it.

**Read it first.** Treat it as current fact. If it disagrees with the code, it is stale, and
fixing it is part of your work — say so, then fix it.

**Write it before you stop.** Not only at the very end: update it whenever the state of play
changes materially — a spike answered, a decision made, a direction abandoned.

It carries five things and stays under a page:

| Section | Contents |
|---|---|
| **Where we are** | Current phase, and the one sentence a newcomer needs. |
| **What just happened** | Last session's work, dated, linking to what it produced. |
| **The next action** | One concrete next move. Not a backlog — the single next step. |
| **Blocked / needs a human** | Decisions only the user can make, and what waits on them. |
| **Do not re-do** | Approaches already tried and rejected, with the reason. |

Rules:

- **Rolling snapshot, not a changelog.** Git is the changelog. Overwrite freely — a handoff that
  accumulates history stops being read.
- **Absolute dates.** Never "yesterday" or "last session".
- **Link, do not restate.** Findings belong in ADRs, architecture in `CLAUDE.md`.
- **Record what failed.** A session that burned two hours on a dead end produced a real result.
- **A stale handoff is worse than none**, because it is trusted. Fix it before continuing.

## Working agreements

Full list in `CLAUDE.md` section 8. The ones agents get wrong most often:

- **Write the ADR when the decision is made**, not later. Every ADR has a "What failed" section,
  and it is not optional — a spike that records only what worked is half a spike.
- **`WinMux.Core` must not reference any platform assembly.** Enforced by a test, not by
  discipline. If the layout engine needs an `HWND`, the design has gone wrong.
- **No silent failure around embedding or persistence.** Surface what happened and what the user
  can do. A pane that vanishes without explanation is worse than one that never opened.
- **Keep the README honest.** Overpromising on app compatibility is the fastest way to make this
  project look broken.
- **Spikes are throwaway**, live in `spikes/`, and are deleted once their ADR is settled.

## Reporting results

State plainly what was measured and what was not. Spike 3 produced a green result on the
scenario that mattered most and it was *unmeasured*, not passing — that distinction was the most
valuable thing the spike produced. If a harness cannot see the thing it was built to test, say so
in the ADR and in `HANDOFF.md`, and leave the question open rather than closing it.
