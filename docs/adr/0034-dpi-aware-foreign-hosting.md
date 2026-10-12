# ADR 0034 — Attach apps that lose DPI scaling when embedded

**Date:** 2026-10-12  
**Status:** Accepted

## Context

A DPI-unaware window can look much smaller when reparented into a per-monitor DPI-aware host.
IExpress rendered at 100% inside a pane on a 150% display while Windows scaled it on the desktop.
Its measured awareness is 0 (unaware); the host monitor reported 144 DPI.

## Decision

PaneHost reads the target window's DPI awareness and its host monitor DPI before embedding. At a
scale other than 100%, unaware and system-aware windows use attach mode. The protocol reports why
and returns the effective strategy. Per-monitor aware windows retain embed mode. The IExpress
observation is recorded in the quirks database as an attach rule with its measured awareness.

## Verification and limits

A native IExpress probe reported awareness 0, host DPI 144, a readable-scaling attach fallback,
and a ready window in 1,227 ms with a 1,000 ms settle. Moving and resizing the host moved and
resized the attached window. A second adoption and detach restored its exact prior rectangle.
The rule's 227 ms discovery value is an estimate from ready time minus settle, not a direct
window-discovery trace. Mixed-DPI moves between monitors were not measured. The current two
monitors are both 150%.

## What failed

The existing mixed-DPI hosting behavior allowed cross-process embedding but did not preserve
Windows bitmap scaling for the unaware child. Forcing a different DPI context on the host window
could affect other applications and coordinate conversion; attach mode kept Windows responsible
for scaling this class of application.
