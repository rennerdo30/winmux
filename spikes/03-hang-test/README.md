# Spike 3 — hang test

Phase 0, spike 3. Answers: *does a wedged pane take the shell down with it, and does an
out-of-process host prevent that?*

Result and decision: [ADR 0001](../../docs/adr/0001-out-of-process-pane-hosts.md).
Read that first — the headline finding is not the one the spike set out to test.

## Running it

```powershell
.\run-matrix.ps1              # full matrix + summary table, ~3 minutes
```

Windows appear and disappear on your desktop while it runs. Individual scenarios:

```powershell
dotnet build -c Release
$exe = ".\bin\Release\net10.0-windows\HangSpike.exe"

& $exe --role shell --scenario T4 --logdir .\logs        # sync layout (default)
& $exe --role shell --scenario T2 --logdir .\logs --asyncpos
& $exe --role shell --scenario T2 --logdir .\logs --nolayout
```

## Shape

One binary, three roles, so the processes are real but the scaffolding is not.

| Role | What it is |
|---|---|
| `hangapp` | Stand-in foreign app. Sleeps on its UI thread on command, stopping its message pump. |
| `panehost` | `WinMux.PaneHost` stand-in. Borderless window, launches the app, reparents it in. |
| `shell` | Runs a scenario, samples responsiveness from the UI thread *and* a watchdog thread, prints a verdict. |

IPC is files in the log directory, not named pipes. The channel is not what is under test,
and a file drop cannot itself deadlock against a wedged UI thread.

## Scenarios

| | |
|---|---|
| T1 | attach-mode baseline — app stays top-level, no `SetParent` anywhere |
| T2 | in-process embed — the shell reparents the app into its own window |
| T3 | OOP host, chained — host reparents app, shell reparents host (tests transitivity) |
| T4 | OOP host, positioned — shell only repositions the host's top-level window |
| T5 | lifecycle — host killed hard; does the app survive? |
| T6 | lifecycle — host detaches and quits cleanly; is the app restored exactly? |

Layout modes `sync` / `--asyncpos` / `--nolayout` exist because the spike found two
independent freeze mechanisms and they had to be separated. See ADR 0001.

## Known limits of this harness

- **It cannot see input starvation.** `IsHungAppWindow` and `SendMessageTimeout` measure
  message-loop liveness. The input-queue attachment claim in CLAUDE.md section 5 needs a
  test that synthesizes real keyboard/mouse input; T3's green result does not clear it.
- The app under test is a cooperative stand-in, not a real application. Spike 2 covers
  Notepad / Explorer / Chromium / UWP.
- Single-DPI. Mixed-DPI hosting is declared (`SetThreadDpiHostingBehavior(MIXED)`) but not
  exercised — that is spike 2's job.

Throwaway code, per CLAUDE.md section 7. Delete once ADR 0001 is no longer being questioned.
