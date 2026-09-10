# Spike 4 — capturing the working directory

Phase 0, spike 4. Answers: *how often does each of the three cwd strategies actually work?*

Result and decision: [ADR 0004](../../docs/adr/0004-cwd-capture.md).
Shippable artefact: [`profiles/`](profiles) — the snippets that make shells report their cwd.

## Running it

```powershell
dotnet build -c Release
$exe = ".\bin\Release\net10.0-windows\CwdSpike.exe"

& $exe                       # all four shells, ~4 minutes
& $exe --nowsl               # skip WSL
& $exe --only pwsh           # one shell
& $exe --only pwsh --verbose # dump the pty tail after each measurement
```

## What it measures

Each shell runs twice — stock, and with the profile snippet — through five scenarios:

| | |
|---|---|
| 0 | fresh shell, nothing moved (the launch-cwd fallback should win) |
| A | plain `cd` |
| B | a path with spaces **and** non-ASCII (`with space and ünïcode`) |
| C | `cd`, then a child process runs and **no new prompt fires** |
| D | a **nested, non-cooperating** shell moved deeper |

For each, all three strategies are read simultaneously: OSC 9;9 / OSC 7 scraped from the raw pty
byte stream (`src/OscWatcher.cs`), the PEB current directory of the pane process **and** of its
deepest descendant (`src/Peb.cs`, `src/ProcessTree.cs`), and the launch cwd.

## Headline results

**Layered strategy, at least one method succeeds: 85% (34/40).**

- **PowerShell's PEB is permanently stale** — `Set-Location` does not update the process working
  directory. 20%, and only the never-moved case. Strategy 2 does not work for the default shell.
- **cmd's PEB works** — 80% root, 100% via the deepest descendant.
- **WSL: PEB is meaningless**, not merely unreliable — it returns Windows paths (`C:\WINDOWS`) for
  a Linux shell. OSC 7 only.
- **OSC survives ConPTY**, including spaces and non-ASCII.
- The layers are complementary: OSC covers PowerShell and WSL; PEB-of-deepest covers nested
  shells, where OSC goes stale.

## Known limits of this harness

- One machine, one Debian image, one PowerShell build. The stock WSL OSC 7 result is
  distribution-dependent and must not be assumed.
- No ssh panes, no `wsl --cd`, no Git Bash / MSYS, no zsh (the snippet has a zsh path, untested).
- No 32-bit target: `src/Peb.cs` reads the x64 PEB layout only and reports WOW64 as a known miss.
- Nothing here writes a session file. Persistence, debouncing and timestamped snapshots are
  Phase 2; this spike only establishes which values can be obtained and how often.
- Elevated shells are not covered — `OpenProcess` would fail and the PEB path would report
  access denied, which is handled but untested.

Throwaway code, per CLAUDE.md §7 — except `profiles/`, which is the deliverable and ships.
