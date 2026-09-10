# HANDOFF

Rolling snapshot of where the work stands. Read this before doing anything; update it before you
stop. Format and rules: `AGENTS.md`, or `CLAUDE.md` section 10.

**Last updated:** 2026-09-10

---

## Where we are

**Phase 1 in progress.** Phase 0 is complete (four spikes, four ADRs). The first product code now
exists and is green:

| Project | State |
|---|---|
| `WinMux.Core` | layout tree, pane model, **TOML session persistence**. Only dependency: Tomlyn. |
| `WinMux.Tests` | 101 tests, all passing |
| `examples/` | committed session files; a test loads every one on each build |
| `WinMux.Cli` | `winmux show` / `validate` / `new` — inspects session files, **cannot open panes** |
| `WinMux.Pty` / `.Platform` / `.PaneHost` / `.Shell` | not started |

`dotnet test -c Release` from the repo root is the gate. Design decisions and invariants for the
engine: [ADR 0005](docs/adr/0005-layout-engine.md); session file format:
[ADR 0006](docs/adr/0006-session-file-format.md).

**Stack: .NET 10 + Avalonia 12 + C#.** VT engine adopted (`Terminal.Emulation`) and pty adopted
(`Porta.Pty`), both to sit behind our own interfaces — neither is referenced yet.

## What just happened

**2026-09-10** — Phase 0 completed and published, then Phase 1 begun.

- Repo at **https://github.com/rennerdo30/winmux** (public, MIT). Phase 0 spikes 1–4 with ADRs
  0001–0004; two artefacts ship rather than being thrown away
  (`spikes/02-reparent/quirks-seed.json`, `spikes/04-cwd/profiles/`).
- Built `WinMux.Core` + `WinMux.Tests` → [ADR 0005](docs/adr/0005-layout-engine.md).
  Splits, stacks, ratios, resize, geometric focus, canonical collapse, session round-trip.
- **Session file format settled: TOML** → [ADR 0006](docs/adr/0006-session-file-format.md).
  The tree is *flattened* into `[[windows.nodes]]`; nesting it would give
  `[[windows.root.children.children.children]]`, worse than the JSON it replaced. Panes are flat
  and are the part meant for hand-editing. Saving is atomic; a corrupt file is quarantined, never
  replaced. `SessionFile.Save`/`Load` is the whole surface.
- Added `examples/cmd-and-explorer.toml` (cmd + Explorer side by side on one directory) and a test
  that loads every committed example, so a stale example fails the build rather than a user.
- Added `WinMux.Cli` — the first runnable exe. `winmux show` draws a session's layout with the real
  `Layouter` (one-column gutter) and lists panes with their cwd provenance in plain words;
  `validate` parses and exits non-zero with the reason; `new` writes a starter file. It **cannot
  open panes** and says so in its own output, because there is still no shell.
  Publish with `dotnet publish WinMux.Cli -c Release -r win-x64 --self-contained false -o dist`.
- **Both load-bearing guards were mutation-tested**, and one of them was broken:
  `GetReferencedAssemblies()` cannot see a platform package that is referenced but not yet *called*
  — adding `System.Drawing.Common` to Core left the suite green. Now also asserts on the project
  file's declared dependencies, and that mutation fails as it should.

Constraints from Phase 0 that shape everything still to be written:

1. **The UI thread must never make a synchronous window call against a foreign window.** Hits
   attach mode as hard as embed mode. Pane geometry goes through a dedicated layout thread or
   `SWP_ASYNCWINDOWPOS`.
2. **Never hard-kill a pane host that still owns an embedded window** — detach, then terminate.
3. **ConPTY's round-trip floor is ~0.08 ms**; a full 60 Hz frame is free for rendering.
   PowerShell's ~15.6 ms echo latency is PSReadLine's own — benchmark against `cmd`.
4. **`Terminal.Emulation` has no public source.** Adopt behind `ITerminalEngine`; its types must
   never reach `WinMux.Core` (the platform-free test already forbids `Terminal.*`).
5. **PowerShell's PEB is permanently stale, WSL's is meaningless.** The cwd snippets are mandatory.

## The next action

**Build `WinMux.Pty` and the `ITerminalEngine` seam** — both headless and testable, and both
prerequisites for the shell. Concretely:

1. `WinMux.Pty` wrapping `Porta.Pty` behind a small `IPtySession` (write, resize, output stream,
   exited). Do **not** hand-roll ConPTY; see "do not re-do".
2. `ITerminalEngine` in a new `WinMux.Terminal` project — **not** in Core. Core may now declare an
   approved, platform-neutral package (Tomlyn, per ADR 0006), but `Terminal.*` is on the forbidden
   list and stays there. Surface: write bytes, read the cell grid, resize, cursor, title, response
   callback; the adapter over `Terminal.Emulation` lives behind it.
3. Tests: spawn `cmd`, feed a marker, assert it reaches the grid. `spikes/01-conpty/src/StageB.cs`
   already does exactly this and can be lifted almost verbatim.

Then the Avalonia shell. Keep `dotnet test` green as the gate throughout.

## Blocked / needs a human

- **Mixed-DPI multi-monitor is untested.** Both monitors are 144 DPI, so the scenario `CLAUDE.md`
  calls "where the bugs live" never ran. Set one display to a different scale factor, then re-run
  `spikes/02-reparent/bin/Release/net10.0-windows/ReparentSpike.exe`.
- **Supply-chain call before v1.** `Terminal.Emulation` has no public source. The `ITerminalEngine`
  seam keeps reversing it cheap, but the call still has to be made.
- **Does input-queue attachment actually starve the shell of input?** Unmeasured. Until someone
  builds a harness that synthesizes real input, the shell must not `SetParent` a pane host.

## Do not re-do

**Measurement and testing discipline** — each of these produced a confident, wrong result first:

- **A suite that passes first try deserves to be distrusted.** All 68 layout tests passed
  immediately; mutation-testing found that the platform-free guard could not fail. Break the
  thing a test protects and watch it go red before believing it.
- **Silence is not completion.** A quiet-wait returned while a sleeping `ping` was still running,
  so a whole spike-4 scenario never executed; the same wait raced PSReadLine's autosuggestion and
  made a unicode path look like an OSC failure. Sync on a marker echoed back **twice**.
- **Do not use `GetMessageW` to bound an observation window** — it blocks, and a "3 second" window
  silently became 2m52s.
- **Do not write a verification whose pattern can match the question.** A resize check waited for
  the word `Columns`, which appears in the echo of the typed command.
- **If a test cannot create the condition it claims to test, delete it.** Two DPI tests scored OK
  while testing nothing (`__COMPAT_LAYER` does not override a manifest, and neither does a runtime
  call when the app's own manifest declares awareness).
- **Do not accumulate unbounded text in a harness** — an O(n²) collector made the first throughput
  numbers measure the harness rather than ConPTY.
- **Separate comparison form from display form** — folding path separators in one shared helper
  printed Linux paths as `\tmp\foo`.
- **When interop fails in a way that reading it does not reveal, run a control.** Three re-reads of
  the ConPTY code found nothing; the same test through `Porta.Pty` found the bug in minutes.
- **Never build a test fixture by string concatenation.** Two malformed-session tests appended an
  extra `[[windows.nodes]]` block *after* `[[windows.panes]]` — which replaces the earlier array
  rather than extending it — so the "duplicate id" fixture contained no duplicate and the "orphan
  node" fixture had lost its root instead. Both passed for the wrong reason. Spell fixtures out.
- **Do not write against a remembered library API.** `Tomlyn.Toml.Parse().ToModel()` does not exist
  in 2.10; the route is `TomlSerializer.Deserialize<TomlTable>`. Reflect over the shipped assembly
  when a call does not compile, rather than guessing a second time.

**Platform traps:**

- **Do not hand-roll ConPTY.** It fails silently and convincingly: conhost starts and the title
  updates while the child's output goes to the host's console, because `CreateProcess` propagates
  the host's std handles and those beat the pseudoconsole. Fix is `STARTF_USESTDHANDLES` with null
  std handles (`spikes/01-conpty/src/ConPty.cs`). Prefer `Porta.Pty`.
- **`GetParent` returns the OWNER for a `WS_POPUP` window** — use `GetAncestor(hwnd, GA_PARENT)`.
- **Capture `GetLastError` on the line after `SetParent`** — intervening calls clobber it, turning
  "UIPI denied (5)" and "window refuses (87)" into a useless "last error 0".
- **Match windows on process image name, never the launched pid** — Win11's `notepad.exe` is a shim.
- **Do not attempt a PEB read for a WSL pane** — it returns a confidently wrong Windows path.
- **A pane's actual rect will not equal its requested rect** (DPI rounding, minimum sizes). Never
  assert equality.

**Environment:**

- **`gh` is not on this session's PATH** (winget-installed mid-session). Invoke as
  `"C:\Program Files\GitHub CLI\gh.exe"`, or open a fresh terminal.
- **`E:\Development\winmux` resolves to `D:\Development\winmux`** (subst or junction).
- **`dotnet package search --exact-match --format json` returns unusable rows.** Query
  `https://azuresearch-usnc.nuget.org/query?q=packageid:<id>` directly.
