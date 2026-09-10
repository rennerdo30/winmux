# cwd reporting profile snippets

Opt-in snippets that make a shell report its working directory, so WinMux can restore a session
into the right place. Strategy 1 of the three in CLAUDE.md section 4, and the **only** one that
works for PowerShell and for WSL — see [ADR 0004](../../../docs/adr/0004-cwd-capture.md).

Measured effect (spike 4, 5 scenarios per shell):

| shell | without snippet | with snippet |
|---|---|---|
| pwsh / powershell | 0% | **80%** |
| cmd | 0% | **100%** |
| bash under WSL | 80% (this Debian already reports) | 80% |

Install these and WinMux restores the right directory. Skip them and PowerShell panes will
restore to wherever the shell was launched, because PowerShell's `Set-Location` does not update
the process working directory that WinMux could otherwise read.
