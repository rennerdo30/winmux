# ADR 0033 — Verified foreign-window reattachment

**Date:** 2026-10-12  
**Status:** Accepted

## Context

On shell shutdown, a foreign application is detached and left open. The previous session saved
only its launch program, so restoring launched another copy. Each restart accumulated windows.
An HWND alone cannot identify an application across restarts because Windows can reuse handles.

## Decision

The session saves the hosted HWND, owning PID and UTC process start ticks before detach. On
restore, PaneHost adopts that HWND only when all three still match. A mismatch leaves an explicit
placeholder; the user may choose **Launch new**. WinMux never silently launches a replacement
when a saved adoption cannot be verified. New windows chosen in the picker may be adopted once
without a saved identity, then gain an identity after PaneHost reports one.

The **Reattach surviving applications** setting defaults on. Turning it off leaves the same
placeholder and requires an explicit launch. The setting never turns a failed identity check into
an automatic launch. The identity check lives in PaneHost so the shell does not query foreign
window handles directly.

## Verification and limits

Launch-spec and settings round-trip tests cover the protocol and preference. A native PaneHost
test detached and re-adopted Character Map with the same HWND and PID, then rejected a wrong PID.
An isolated full WinMux restart also retained the same Character Map PID with one copy. The
shell build now builds PaneHost before copying it beside WinMux, since the earlier solution build
left a stale PaneHost executable in the shell output.
If a process hides its start time from WinMux, the safe fallback is the placeholder. Window
identity is a safety check, not a way to restore an application's internal state.

## What failed

Saving the HWND alone would sometimes adopt an unrelated window after handle reuse. Re-launching
whenever reattachment fails reproduces the duplicate-window bug when the original app is still
open with a changed HWND. Closing every application on exit would also risk unsaved documents and
does not provide the requested detached-window behavior.
