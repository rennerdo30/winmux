# Spike 1 — ConPTY and the terminal stack

Phase 0, spike 1. Answers the question that gated everything: *adopt a VT parser/renderer, or
write one — and does the proposed stack survive the answer?*

Result and decision: [ADR 0002](../../docs/adr/0002-terminal-stack.md).

## Running it

```powershell
dotnet build -c Release
$exe = ".\bin\Release\net10.0-windows\ConPtySpike.exe"

& $exe                      # stage A: spawn / echo latency / throughput / resize, per shell
& $exe pwsh                 # stage A for one shell only
& $exe --stageb             # stage B: VT engine evaluation, headless
& $exe --diag               # minimal "does one marker survive the pipe" check
& $exe --diag --freeconsole # same, with the host's console released
& $exe --control            # same check through Porta.Pty, as a control
```

Stage B reads the payload file stage A generates, so run stage A first.

## What it measures

**Stage A** hand-rolls ConPTY (`src/ConPty.cs`) deliberately — the point is to establish what the
platform costs before any library is involved.

- **spawn** — time to first byte and to a settled prompt
- **echo latency** — write one keystroke, wait for it to come back. 40 samples after warm-up.
  This is the input-lag number.
- **throughput** — dump a 4.3 MiB file through the pty and measure MiB/s
- **resize** — `ResizePseudoConsole`, then ask the shell its own size and check it agrees

**Stage B** (`src/StageB.cs`) drives `Terminal.Emulation` headlessly: raw parse throughput,
end-to-end through a real pwsh, reflow across resizes, alternate screen, CJK double-width cells
and OSC 8 hyperlinks.

## The trap that ate this spike

A hand-rolled ConPTY host will *appear* to work — conhost starts, the title updates, init and
teardown sequences arrive — while the child's actual output goes to the **host's** console
instead of the pty. `CreateProcess` propagates the host's standard handles, and when the host is
a console app those beat the pseudoconsole.

Fix: `STARTF_USESTDHANDLES` with all three std handles null (`src/ConPty.cs`, near the
`suppressStdHandleInheritance` flag). Microsoft's sample never shows this because its host is a
GUI app whose std handles are already null.

`--control` exists because of this: when interop fails in a way that reading it does not reveal,
run the same test through a known-good library and diff the behaviour.

## Known limits of this harness

- **Nothing here renders.** Stage B validates the engine's *grid*, not glyph rasterisation, font
  fallback, or paint latency. Those are Phase 1 problems and need a real Avalonia surface.
- Throughput varies run to run by roughly ±30%; treat the figures as an order of magnitude.
- Single machine, single DPI, one Windows build. No WSL or ssh panes were exercised — spike 4
  covers WSL for cwd capture.
- `--control` uses `Porta.Pty`; the spike does not otherwise depend on it.

Throwaway code, per CLAUDE.md §7. Delete once ADR 0002 is no longer being questioned.
