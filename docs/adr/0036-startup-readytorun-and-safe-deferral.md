# ADR 0036 — ReadyToRun startup and safe post-open registration

**Date:** 2026-10-12  
**Status:** Accepted

## Context

WinMux's first visible window took around 1.1–1.3 seconds in the owner report. A Release build
JITs Avalonia and the shell at launch. App Paths registration also ran synchronously before the
window, though it need not decide whether this process may open a session.

## Decision

The local package workflow publishes the shell and PaneHost with ReadyToRun. PaneHost must be
published separately after the shell, because the shell's project reference otherwise copies its
ordinary build. App Paths registration runs after the first window opens and reports a failure in
the status line. The process-start-to-first-`Opened` duration is written to `crash.log` off the UI
thread.

The legacy-instance probe stays before session load. It decides whether an older WinMux owns the
same session and command endpoint; deferring it could open and save a duplicate session before
the conflict is known. This safety boundary outranks a small startup gain.

## Verification and limits

Five isolated one-terminal runs from `Process.Start` to a visible top-level window gave a median
of 874.4 ms for a local framework-dependent publish without ReadyToRun and 510.7 ms with it, a
42% reduction. The first newly published binary took 3,008 ms without ReadyToRun and 1,008 ms
with it, but those first runs include uncontrolled disk-cache and security-scanner effects and
are not treated as a cold-start percentage. At one second after opening, working set averaged
about 157 MB before and 146 MB after. `crash.log` recorded a 512 ms first-open duration for the
510.7 ms external run. A local ReadyToRun shell plus PaneHost survived a full Character Map
detach/restart/re-adopt check with one application process. No release package or installer was
built.

The isolated profile intentionally skips App Paths registration and the legacy probe, so these
measurements isolate ReadyToRun rather than the registration deferral. The old-instance safety
path still needs its normal environment for verification.

## What failed

Simply setting ReadyToRun on the shell left PaneHost as a normal build artifact because the
shell's copy target uses `GetTargetPath`. Publishing PaneHost into the same local destination after
the shell fixed that. Moving the legacy probe after first render would improve a timing number by
crossing the single-instance safety boundary, so it was rejected.
