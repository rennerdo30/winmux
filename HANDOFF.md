# HANDOFF — WinMux

## Where we are

**WinMux 0.8.3 is published**, from `dea0b00`, as the latest stable release:
[installer, zip and checksums](https://github.com/rennerdo30/winmux/releases/tag/v0.8.3).
Ordinary startup restores the saved layout without adding CMD. See
[ADR 0041](docs/adr/0041-explicit-startup-folder-intent.md).

## What just happened

**2026-10-22:** [PR 1](https://github.com/rennerdo30/winmux/pull/1) fixed WM-20:
restore alone queues no terminal; `--open-directory <folder>` explicitly opens one
on cold or warm startup, and bare warm forwarding remains. Existing saved tabs stay.
[PR 2](https://github.com/rennerdo30/winmux/pull/2) fixed WM-21 test scheduling and
failure reporting without changing product save behavior or timing bounds.
Separate Sol and Claude reviews approved both bundles.
[Release CI 39677781999](https://github.com/rennerdo30/winmux/actions/runs/39677781999)
passed: **1,359 tests passed, 6 live FTP/SFTP skips**, package and installer
install/uninstall. Downloaded zip/setup SHA-256 values match checksums.txt; packaged
shell reports `0.8.3+dea0b00...`, GUI/CLI PE subsystems verified. GitHub latest API
returns stable `v0.8.3`. Local receipts: `artifacts/release-v0.8.3/`.

## The next action

Update through About WinMux, close safely and reopen repeatedly: confirm no new CMD
appears, then check an explicit folder launch. WM-20 is waiting for native owner review.

## Blocked / needs a human

Native Explorer/foreground and physical mixed-DPI acceptance remain unmeasured.
Existing owner checks remain open. Installer signing still needs an owner certificate.
The patch is published; no release authorization remains outstanding.

## Do not re-do

Do not infer folder intent or remove previously saved CMD panes automatically.
Keep session errors loud, test profiles isolated, lockfiles unchanged and versioning
from MinVer tags. The failed `v0.8.2` tag is retained; no release assets were published
for it. Save acceptance tests retain 1s/200ms bounds and negative controls in an isolated
xUnit collection; report primary and cleanup failures together. CI contention causation
was not proven. Do not treat headless checks as native acceptance.
