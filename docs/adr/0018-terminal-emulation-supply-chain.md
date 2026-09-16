# ADR 0018 — `Terminal.Emulation`: the supply-chain position

**Date:** 2026-09-15
**Status:** Accepted for 0.x. **The v1 call is not made here** — see "The decision, and who makes it".
**Extends:** [ADR 0002](0002-terminal-stack.md).

## Context

[ADR 0002](0002-terminal-stack.md) adopted `Terminal.Emulation` rather than writing a VT engine,
because it parses at 14–36 MiB/s with correct reflow, alternate screen, double-width cells and
OSC 8 — and wrote the caveat down at the same time: *"a 5-week-old single-author package whose
source repository is not public"*. It has been listed as a decision owed before v1 ever since,
without anyone checking whether the facts had changed.

They have, a little, and not in the direction that would settle it.

## What is true as of 2026-09-15

| | |
|---|---|
| Version in use | 0.3.3, published 2026-09-09 |
| Latest published | 0.3.4, 2026-09-11 |
| First published | 0.1.0, 2026-08-06 — **the entire history is five weeks old** |
| Releases | 9 in those five weeks; actively maintained |
| Total downloads | ~1.2K, about 30/day |
| Licence | **MIT** |
| Author | a single person (`b-y-t-e`) |
| Declared repository | `https://github.com/b-y-t-e/Terminal.Avalonia`, pinned to commit `dade5bb5…` |
| Does it resolve? | **No. 404 from both an unauthenticated fetch and the GitHub API.** |

Two of those are worth separating, because ADR 0002 ran them together.

**The licence is MIT**, which is the good half. It permits vendoring, forking, decompiling and
shipping a modified copy. Nothing legal stands between WinMux and maintaining this code itself.

**The source is still not available**, which is the bad half — and it is now worse than "not
published", because the package *claims* a repository and an exact commit that cannot be reached.
That is either a repository made private after publishing, or one never made public. Either way the
pinned commit is unverifiable, so the metadata offers the appearance of provenance without any.

## Who publishes it

Worth separating from "single author", which sounds like an anonymous account and is not.

`b-y-t-e` is a GitHub account created in **2013** with **37 public repositories**, most of them
unrelated day-job work going back a decade. This is a long-standing developer publishing under their
own name (the package copyright reads "Andrzej Ból"), not a throwaway identity. That does not make
the code reviewable, but it substantially changes the *deliberate supply-chain attack* reading,
which is the scenario that usually motivates this kind of concern.

`Terminal.Avalonia` is specifically **absent** from those 37 — so it was made private or deleted,
rather than never linked.

One useful thing turned up in the same search: five weeks before first publishing
`Terminal.Emulation`, the same account **forked
[`tomlm/Iciclecreek.Avalonia.Terminal`](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal)** —
MIT, public, 30 stars, actively updated. Whether `Terminal.Emulation` descends from it is unknown
and unprovable from here, but it exists, it is readable, and it does the same job. That matters for
the cost of option 4 below: a replacement would not start from nothing.

## What is already true in our favour

- **The seam holds.** `ITerminalEngine` in `WinMux.Terminal` is ours, and ADR 0002 required that
  the package's types never reach `WinMux.Core`. That is enforced and still true: the adapter is the
  only thing that knows this package exists. A replacement re-implements one interface.
- **The engine is not where the risk usually is.** It opens no sockets, spawns no processes and
  reads no files; it is a parser over a byte stream. The blast radius of a hostile update is smaller
  than for most dependencies — though "smaller" is not "small", since it does see every byte a
  terminal produces.

## Decision, for 0.x

**Keep the dependency, and remove the part of the risk that needs no decision.**

`RestorePackagesWithLockFile` is on for the whole solution, so `packages.lock.json` now records the
resolved version and the **content hash** of every package, `Terminal.Emulation` included:

```
"Terminal.Emulation": { "resolved": "0.3.3",
  "contentHash": "rKT+Ccu5knBeGXC5y6FwgSDz5jHoiICYMj+tR7ZTZNafet8kZDZeDxvlkIj/h0uZexcWs2/2KHPJaZYOyZytUQ==" }
```

This closes the worst version of the problem — a package replaced under a version number already in
use, restored silently on someone else's machine, and shipped — without pre-empting the judgement
call. A restore that no longer matches the lock now fails loudly.

It does **not** address the case that actually motivates the concern: a future version that is
hostile and correctly published. Only reading the source would, and there is no source to read.

## The decision, and who makes it

Before v1, one of these has to be chosen. The engineering is roughly costed; the judgement is not
an engineering question.

1. **Accept and pin.** Stay on a hashed, known-good version and update only deliberately. Cost:
   nothing now. Risk: an unreviewable dependency in the data path forever, and no security fixes
   unless the author ships them.
2. **Vendor the binary.** Commit the `.dll` (MIT permits it) so the build survives the package being
   unlisted or the author disappearing. Cost: an afternoon, plus a binary in the repository. Risk:
   unchanged — still unreadable, now also unpatchable.
3. **Ask the author to publish the source.** Free, might work, and would settle it outright. Nobody
   has asked.
4. **Replace it.** The `ITerminalEngine` seam means the shell does not change. ADR 0002 measured
   what a *from-scratch* engine takes — a correct VT500 parser with reflow, alternate screen,
   double-width cells and the query/response sequences TUIs hang without — which is weeks, with a
   regression surface the size of every terminal program anyone runs. But it need not be from
   scratch: `tomlm/Iciclecreek.Avalonia.Terminal` is public, MIT and maintained, and adapting it
   behind the existing seam is a far smaller job than writing a parser. Nobody has evaluated it
   against ADR 0002's benchmark.

**Recommendation: 3, then 1, with 4 costed properly first.** Asking costs nothing and could remove
the objection entirely; pinning is already done. Before treating 4 as expensive, run ADR 0002's
benchmark against `Iciclecreek.Avalonia.Terminal` — if it is close, the whole question becomes
"swap to the readable one", which needs no negotiation with anybody.

## Consequences

- `packages.lock.json` is committed for all 17 projects. Changing a dependency now changes the lock
  file in the same commit, by design.
- The version stays 0.x until this is settled, which is what `Directory.Build.props` already says.
- CLAUDE.md's "source repository is not public" was right and is now specific: the repository is
  *declared and unreachable*, which is a different and slightly worse thing than absent.

---

## Addendum, 2026-09-16: the cost stopped being hypothetical

A user reported a pane where **every line was underlined**, including plain `cmd` output that had
asked for nothing. Two separate engine defects came out of it, and the one that mattered was not the
one that looked obvious.

**The cause: `ESC[>4m`.** The `>` makes it a *private* CSI sequence — xterm's "set modifyOtherKeys",
a keyboard-protocol setting with nothing to do with colour. `Terminal.Emulation` ignores the prefix
and reads the rest as `ESC[4m`: underline on. Claude Code sends it once while starting up and then
styles almost nothing, so no SGR 24 or reset ever follows, and every line printed from then on — in
that program and in the shell after it exits — is underlined. `ESC[?4m` and `ESC[<4m` do the same.

**Also found, and real, but not this bug: `ESC[4:0m`.** Underline *off* in the colon sub-parameter
form (`4:1` single, `4:2` double, `4:3` curly). The engine reads the sub-parameter as if absent, so
`4:0` turns underline on when asked to turn it off.

Both confirmed in 0.3.3 and 0.3.4, and pinned by `PrivateModeSgrTests` and
`UnderlineSubParameterTests`.

**How the real cause was found, after the wrong one had been fixed with confidence.** The colon-form
theory was plausible, testable and true, so it was fixed and declared done — and the pane was still
underlined, because that sequence was never in the stream. What settled it was capturing the bytes a
real `claude --resume` emits through a real ConPTY: the capture contained **no SGR at all**, only
cursor positioning and the private-mode handshake. A third theory, SGR 21, was also tested and
rejected the same way; the engine's reading of it follows ECMA-48 and xterm, and changing it would
have been a guess dressed as a fix.

The lesson is the one CLAUDE.md section 7 already states and this session relearned: *measure the
input before theorising about the output.* A ten-minute capture harness would have gone straight to
the answer.

**There was nowhere to send a patch.** That is the entire point of this ADR, and it arrived as a
real bug rather than an argument: the declared repository still 404s, so the options were to fix it
from outside or ship it broken. `SgrColonNormalizer` in `WinMux.Terminal` now repairs the byte
stream before the engine sees it: a CSI with a private prefix that ends in `m` is dropped outright —
there is no such thing as a private SGR, and the engine has no use for modifyOtherKeys because
WinMux handles input itself — and `4:N` is rewritten into the plain form, `4:0` to `24` and every
other style to `4`. It is stateful, because ConPTY splits writes wherever it likes and `ESC[4` and
`:0m` routinely arrive in different reads. Private sequences with any other final byte still pass
through untouched: `ESC[?25l` and `ESC[?2004h` are cursor visibility and bracketed paste, which the
engine does need.

Two things this changes about the decision above:

- **Option 4 gained weight and option 3 lost it.** An unreachable author cannot take a bug report,
  so "ask them to publish the source" is not merely unanswered — it is the option whose value
  depends on someone who has not responded to anything. Meanwhile the seam did its job: the fix
  landed in the adapter, in about a hundred lines, without the shell knowing.
- **This is unlikely to be the only one.** A parser that ignores colon sub-parameters in SGR 4 will
  do the same elsewhere, and each instance will be found the same way — by a user, in a screenshot.
  The workaround does not generalise; the next one needs its own.

The recommendation is unchanged in shape but sharper in urgency: **benchmark
`Iciclecreek.Avalonia.Terminal` against ADR 0002's numbers before v1.** The question is no longer
only "can the source be read" but "who fixes the next one".
