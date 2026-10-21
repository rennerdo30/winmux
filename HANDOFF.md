# HANDOFF — WinMux

## Where we are

WinMux 0.8.1 is published; WM-20 is fixed on the candidate for patch **0.8.2**.
Ordinary startup restores the saved layout without inserting CMD. See
[ADR 0041](docs/adr/0041-explicit-startup-folder-intent.md).

## What just happened

**2026-10-22:** `8ddbb0d` separates restore from explicit folder intent. Use
`WinMux.exe --open-directory <folder>` for a cold or warm folder launch; bare warm
launches still forward the caller folder. Fresh layouts reuse their default CMD.
Existing saved panes are preserved. Focused startup/forwarding/keymap checks:
**55 passed**, Release solution compile: **0 warnings/errors**. The negative
control failed on the original queue wiring by detecting an unwanted second CMD.
Evidence is recorded on **WM-20**. Independent review and release CI are pending.

## The next action

Complete the separate Sol review, Claude final review and CI gates, then publish
owner-authorized `v0.8.2` through the existing tag-driven release workflow.

## Blocked / needs a human

Native Explorer/foreground and physical mixed-DPI acceptance remain unmeasured.
Existing owner checks remain open. Installer signing still needs an owner certificate.
No new owner decision blocks this authorized patch release.

## Do not re-do

Do not infer folder intent from the working directory or parent process. Do not
remove previously saved CMD panes automatically. Keep session access failures loud,
user data intact, isolated test profiles, unchanged lockfiles and MinVer tag versioning.
The old startup rule in ADR 0029 is superseded by ADR 0041. Native acceptance is
separate from headless checks. Full suites run on CI.
