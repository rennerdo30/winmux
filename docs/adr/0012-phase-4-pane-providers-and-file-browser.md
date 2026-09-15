# ADR 0012 — Phase 4 pane providers and built-in file browser

- **Status:** accepted
- **Date:** 2026-09-13
- **Code:** `WinMux.Panes`, `WinMux.Shell/FileBrowser`, `WinMux.Shell/Panes`
- **Acceptance:** `scripts/phase4-demo.ps1`

## Context

The Phase 3 shell selected terminal and foreign-app behavior with a closed `PaneKind` enum and a
large type switch in `MainWindow`. A fourth pane required edits across Core, persistence, CLI, and
shell lifecycle code. That contradicted the product claim that pane types are providers and made
the Phase 4 file browser likely to duplicate focus, state capture, and shutdown rules.

The file browser also has a different process model. It is trusted WinMux UI and does not benefit
from a subprocess boundary, while terminal and foreign-app workloads still do. The architecture
needed to state that exception instead of preserving the inaccurate claim that every pane is a
process.

## Decision

1. `PaneKind` is a validated, case-normalized stable string identifier rather than an enum.
   Built-ins retain `terminal`, `file-browser`, and `foreign-app`; third-party identifiers such as
   `com.example.preview` round-trip without Core changes. A pane and its restore descriptor must
   carry the same kind.

2. The public, OS-neutral `WinMux.Panes` assembly owns `IPaneProvider`, `IPaneRuntime`, provider
   context/registry, layout notification, state capture, focus, close preparation, and disposal.
   It references Core and Avalonia, but no Win32, shell, PTY, or PaneHost project. Each shell window
   owns a registry. Runtime assembly discovery and plugin installation are deliberately not part
   of Phase 4.

3. Terminal, foreign-app, and file-browser behavior are providers. `MainWindow` owns only layout,
   action routing, chrome, and generic runtime coordination. An unavailable provider or failed
   restore creates a visible placeholder that retains the original descriptor. Shutdown preserves
   Phase 3's mixed result: reusable runtimes remain alive if a sibling blocks, while an already
   detached foreign pane is removed and never silently relaunched.

4. The built-in file browser is a trusted in-process Avalonia runtime. It enumerates off the UI
   thread, cancels superseded work, sorts directories before files deterministically, and supports
   path entry, parent, refresh, mouse/keyboard directory navigation, and independent pane state.
   Current directory and selection are continuously stored in descriptor extras. If a restored
   directory is unavailable, the pane visibly uses a fallback but preserves the saved path until
   the user deliberately navigates elsewhere.

5. Terminal handoff resolves a selected directory to itself; a selected file or no selection uses
   the browser's current directory. `new-file-browser` and `open-terminal-here` are named actions
   exposed through keymap, palette, and CLI (`winmux files`, `winmux terminal-here`). CLI dispatch
   awaits asynchronous pane creation and reports its real completion or failure.

## Verification

On 2026-09-13, the Release solution built with zero warnings and all **280/280 tests passed**.
Coverage includes custom provider registration from a separate test assembly, provider boundary
dependencies, unknown-kind TOML round-trip, pane/descriptor invariants, asynchronous dispatch,
independent file panes, deterministic enumeration, Unicode and spaces, restore/fallback behavior,
terminal handoff, and cancellation during enumeration.

`scripts/phase4-demo.ps1 -VerifyOnly` generated and validated a four-pane session and passed 23
focused tests. Normal mode opens two independent file browsers, a terminal, and a missing-directory
restore case, then validates the saved descriptor fields after close. The shell window launched
successfully with that fixture; native-window capture was unavailable in the automation bridge, so
the interactive visual assertions remain a human walkthrough rather than a claimed automated pass.

The Phase 3 verifier was rerun after the refactor: Character Map embed passed, and Calculator's
Win32 error 87 still fell back to attach with the expected notice. Both checks detached cleanly.

## Consequences

- New pane kinds no longer require changes to Core or the layout tree, and unknown descriptors are
  not destroyed merely because their provider is absent.
- Provider authors get a public contract, but must currently be registered by application code;
  discovery, packaging, trust, and version compatibility remain future work.
- The file browser is intentionally not process-isolated. Filesystem failures are contained and
  visible, but a bug in trusted browser UI can affect the shell process.
- Phase 5 can move platform operations behind `WinMux.Platform` without changing provider or
  layout contracts.

## What failed

- The closed `PaneKind` enum made the first provider draft extensible only in name. Replacing it
  with a validated value type was necessary for a provider in another assembly to define a kind.
- The first navigation wrapper canceled `Task.Run` scheduling but passed the lifetime token into
  enumeration, so superseded directory reads could continue. The operation token now reaches each
  filesystem iteration, with a regression test that cancels during enumeration.
- Synchronous action dispatch returned before async pane creation completed, which could make the
  CLI report success for a later failure. The dispatcher now has an awaited path for CLI callers
  and a completion event for keymap/palette background work.
- The first Phase 3 regression command used Windows PowerShell 5.1, while its verifier requires
  PowerShell 7. Rerunning with `pwsh` passed; this was a harness invocation error, not a product
  failure.
- The native computer-control bridge exposed browser tabs but no native applications. It could not
  inspect the launched WinMux window, so no visual result is inferred from that unavailable sensor.
