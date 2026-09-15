# Spike 5 — does input-queue attachment starve the shell?

Answers the last unmeasured claim in CLAUDE.md section 5, and the open question in
[ADR 0001](../../docs/adr/0001-out-of-process-pane-hosts.md) finding 4.
The conclusion is [ADR 0017](../../docs/adr/0017-input-queue-attachment.md).

## Running it

```powershell
dotnet run -c Release --project . -- --mode detached
dotnet run -c Release --project . -- --mode attached
```

Both open two windows, type ten synthesized keystrokes at the host window, wedge the app for six
seconds, type ten more, and then time how long the host takes to reclaim the foreground.

`detached` is the shipping topology: two unrelated top-level windows.
`attached` is the one the architecture forbids: the app's window `SetParent`'d into the host's,
which attaches the two threads' input queues.

**Do not run it on a machine you are using.** It synthesizes keyboard input and steals the
foreground; `VK_F13` is used precisely because nothing else responds to it.

## What it found

| | detached | attached |
|---|---|---|
| keys received while the app is wedged | 10/10 @ 1.1 ms | 10/10 @ 0.9 ms |
| taking focus while the app is wedged | 201 ms | **4,386 ms** |

Input is not starved. Focus blocks for the whole wedge. The constraint stands; the reason given for
it did not.

## The harness has a control case, and it is not decoration

The first version reported starvation in *both* modes — and also reported zero keystrokes arriving
with the app perfectly healthy, which nothing was checking. `SetForegroundWindow` is refused to a
process that is not already in the foreground, and `SetFocus` only works from the thread owning the
window; neither failure surfaced anywhere.

So the harness now refuses to report a result it has not earned: it aborts if it cannot take the
foreground, and aborts if zero keys arrive in the healthy phase. Any measurement harness in this
repository should do the same — see ADR 0017's closing section for why that is now a rule.
