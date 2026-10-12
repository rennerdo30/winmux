# ADR 0032 — Safe foreign-window discovery

**Date:** 2026-10-12  
**Status:** Accepted

## Context

PaneHost's class-name mode accepted any window of a class. `Chrome_WidgetWin_1` belongs to VS Code
and several unrelated Chromium applications; `ApplicationFrameWindow` is shared by packaged apps.
When no newly observed window matched, PaneHost fell back to a pre-existing match, which could
only be someone else's window. Reparenting it would take that window away from its owner.

## Decision

Class-name discovery also requires the expected window process image. The launch plan passes the
rule's explicit process name or its measured window executable; an unknown class override uses the
launched program image. Calculator's packaged frame additionally needs a Calculator title, since
Settings can share both class and `ApplicationFrameHost.exe`. PaneHost only considers windows that
appeared after launch and never falls back to a pre-existing one. If none appears, it explains that
the user can choose an existing window with the explicit Adopt action.

An explicit adoption remains a separate user action and can name a pre-existing window.

## Verification and limits

Candidate tests reject Chrome for VS Code and Settings for Calculator while accepting the intended
images. The launch-plan test checks that the frame-host image reaches PaneHost. An English
Calculator title works; a localized title may require a local quirks override or explicit adoption.
The new-window check does not prove causality if another window of the same app opens concurrently.

## What failed

Matching a class alone appeared to support launchers that hand their window to another process,
but it also matched unrelated applications. Treating any pre-existing match as a fallback hid
launch failures and created the most dangerous possible false positive. Both shortcuts were
removed rather than weakened by a longer settle delay.
