# ADR 0004 — Capturing the working directory: ship the shell snippets, they are not optional

- **Status:** accepted
- **Date:** 2026-09-10
- **Spike:** [`spikes/04-cwd`](../../spikes/04-cwd) (Phase 0, spike 4)
- **Artefact:** [`spikes/04-cwd/profiles/`](../../spikes/04-cwd/profiles) — the shippable snippets

## Context

Restoring each pane's working directory is priority 1 and the reason WinMux exists. CLAUDE.md §4
prescribes a layered strategy, best available wins: (1) the shell reports via OSC 9;9 / OSC 7,
(2) query the deepest child process's PEB, (3) fall back to the launch cwd. Spike 4 measures how
often each actually succeeds.

Four shells (pwsh 7.6.6, Windows PowerShell 5.1, cmd, bash on WSL Debian) × with and without the
profile snippet × five scenarios: fresh shell, plain `cd`, a path with spaces and non-ASCII, a
`cd` while a **child process is running**, and a **nested non-cooperating shell** moved deeper.
40 measurements.

## Findings

| shell | snippet | OSC 9;9 / 7 | PEB (root) | PEB (deepest) | launch cwd |
|---|---|---|---|---|---|
| pwsh | off | 0% | **20%** | 60% | 20% |
| pwsh | **on** | **80%** | 20% | 60% | 20% |
| powershell 5.1 | off | 0% | **20%** | 60% | 20% |
| powershell 5.1 | **on** | **80%** | 20% | 60% | 20% |
| cmd | off | 0% | 80% | **100%** | 20% |
| cmd | **on** | **100%** | 80% | **100%** | 20% |
| bash (WSL Debian) | off | **80%** | **0%** | **0%** | 0% |
| bash (WSL Debian) | on | **80%** | **0%** | **0%** | 0% |

**Layered strategy — at least one method succeeds: 85% (34/40).**

**1. PowerShell's PEB is permanently stale, and PowerShell is the default shell.** `Set-Location`
updates PowerShell's *provider* location but not the process's Win32 current directory, so the PEB
still reports the launch directory forever. 20% is exactly the one scenario where nothing had
moved yet. This is the single most consequential result: **strategy 2 does not work for the shell
most panes will run.**

**2. cmd is the opposite** — its PEB tracks `cd` faithfully (80% root, 100% via the deepest
descendant), because `cd` really does call `SetCurrentDirectory`.

**3. For WSL, only OSC works — PEB is not merely unreliable, it is meaningless.** Reading
`wsl.exe`'s PEB returned `E:\Development\winmux\spikes\04-cwd`, and the deepest Windows descendant
returned `C:\WINDOWS`. Both are Windows paths for a shell whose cwd is `/tmp/...`. There is no
Windows process holding the answer; the boundary cannot be crossed by strategy 2 at all.

**4. This Debian image reports OSC 7 with no snippet at all** (80% stock). Distribution-dependent
— a vte.sh-style integration is common — so it is a pleasant default, never an assumption.

**5. OSC survives ConPTY intact**, for both OSC 9;9 (Windows convention, absolute path) and OSC 7
(`file://host/path`), including paths with spaces and non-ASCII characters. This was not obvious
in advance and it is what makes strategy 1 viable at all.

**6. The layers are genuinely complementary, exactly as §4 intends.**
- OSC covers what PEB cannot: PowerShell, and WSL.
- PEB-of-deepest-child covers what OSC cannot: a **nested non-cooperating shell** (scenario D).
  Under pwsh, `cmd /k cd deeper` leaves OSC reporting the outer shell's stale path, while the
  nested cmd's PEB has the right answer — 100% for cmd, 60% for pwsh.
- Launch cwd covers only the fresh case, which is precisely its job.
- A useful accident: cmd's `PROMPT` is an environment variable, so a nested cmd **inherits**
  reporting and scenario D still hits OSC. A PowerShell prompt *function* is not inherited.

**7. The one combination nothing solves: stock PowerShell, after a `cd`, with no child process.**
No OSC, stale PEB, wrong launch cwd. Four of the six failures are this. The other two are a
nested shell under WSL, where neither the nested bash reports nor any Windows PEB can help.

## Decision

1. **Ship the profile snippets and treat installing them as part of setup, not a power-user
   extra.** `spikes/04-cwd/profiles/` holds working `winmux.ps1`, `winmux.cmd` and `winmux.sh`.
   The PowerShell one *wraps* an existing prompt rather than replacing it.

2. **Offer to install them, and say plainly what is lost otherwise.** A PowerShell pane with no
   snippet will restore to the wrong directory whenever the user has changed directory — that is
   the headline feature failing silently, which §8 forbids. Surface it in the UI once, per shell,
   with a one-click fix.

3. **Keep all three layers, and prefer them in this order per pane:** freshest OSC report →
   PEB of the deepest descendant → PEB of the pane's own process → launch cwd. Never fail a
   whole save because one pane is unknown.

4. **Mark WSL panes as OSC-only.** Do not attempt a PEB read across the WSL boundary; it returns
   a confidently wrong Windows path, which is worse than returning nothing.

5. **Record provenance with each persisted cwd** — which strategy produced it, and when. A value
   from a stale OSC report and one from a live PEB read do not deserve equal trust on restore,
   and §4 already requires timestamped snapshots.

## Consequences

- Session restore is reliable for cooperating shells and for cmd, and degrades to the launch
  directory in the one case that matters most (bare PowerShell). That gap is closed by
  installation, not by engineering, so onboarding carries real product weight.
- The quirks-style per-shell knowledge (which strategies apply) belongs in configuration next to
  the shell profile definitions.
- Percent-encoding on OSC 7 is not implemented by the shipped `winmux.sh`; plain paths with
  spaces and non-ASCII round-tripped correctly here, but a strict consumer would expect encoding.
  Worth revisiting before v1.

## What failed

- **Three separate harness bugs each produced a false "cwd capture failed", and all three were
  the same mistake: treating silence as completion.**
  - `WaitQuiet` returned while `ping` was still running, because a sleeping child emits nothing,
    so 400 ms of quiet was satisfied instantly. The nested-shell scenario was never executed at
    all — its commands sat in the input buffer — and scored MISS for every strategy.
  - The same wait raced PSReadLine: it renders the typed line, then its predictive
    autosuggestion, with gaps longer than the threshold. The non-ASCII scenario measured a stale
    prompt and looked like "OSC cannot handle unicode paths". The stock-shell transcript proved
    the `Set-Location` had in fact succeeded.
  - Fixed by syncing on a random marker echoed back **twice** — once as the echoed command line,
    once as the shell's own output. One occurrence is not enough, because the echo alone proves
    only that the shell received the keystrokes.
- **Scenario 0 initially scored OSC as "never reported"** because the watcher was reset
  immediately before measuring, so no prompt had yet fired. An unfair zero is still a wrong
  number; a prompt is now forced after the reset.
- **The reporter mangled Linux paths into `\tmp\winmux-spike4`** by folding separators inside the
  same function used for display. Comparison and presentation are now separate: `Canonical` for
  hit/miss, raw for the human.
