# ADR 0035 — Reuse terminal rows and pace output frames

**Date:** 2026-10-12  
**Status:** Accepted

## Context

The terminal control copied every visible row and created new `FormattedText` objects on every
render. During a large output stream it redrew the entire pane whenever the engine requested a
frame. Input shared the UI dispatcher with that rendering work.

## Decision

The control reuses one cell buffer, caches shaped text for each visible row, and rotates the row
cache when the viewport scrolls. It still compares copied cell content before reuse, so edits,
colour changes and alternate-screen output invalidate the affected row. Font changes clear the
cache. A background-priority timer caps output-driven invalidations at 60 Hz and stops when idle.

## Measurements and limits

An isolated Release WinMux at 1600×1000 DIP streamed the same 22,550,000-byte plain-text file
with varied line numbers. One baseline and one run after the rotating cache, from the command's
start marker to its completion marker, gave:

| Measure | Before | After |
| --- | ---: | ---: |
| Output duration | 17.79 s | 18.34 s |
| WinMux CPU time, including 2 s settle | 40.39 s | 25.44 s |
| Main UI thread CPU time | 11.80 s | 2.48 s |
| Peak working set | 324.6 MB | 257.5 MB |
| Peak private bytes | 429.5 MB | 441.4 MB |
| UI dispatch p95 proxy (`WM_NULL`) | 31.7 ms | 33.6 ms |

The UI thread fell from about 66% to 14% of one core. Total process CPU fell from about 2.27 to
1.39 cores, but it did **not** reach the issue's 0.5-core target. Private memory did not improve
in the matched runs. The `WM_NULL` probe is an input-dispatch proxy, not a typed-character latency
measurement; its p95 varied from 15.8 to 33.6 ms across two later runs. The six skipped live
Shell tests still need their intended environment.

## What failed

The existing headless test application returned no rendered frame from `CaptureRenderedFrame`,
so it could not prove pixel equivalence. A native visual check remains necessary. Merely caching
rows at fixed visual indices wasted most of the cache on each scroll; rotating by the viewport's
first-row delta reduced the measured CPU further. The aggregate CPU result still warrants a
separate parser/render-thread profile before claiming the 0.5-core target.
