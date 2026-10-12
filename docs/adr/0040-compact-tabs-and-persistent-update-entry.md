# 0040 — Compact tabs and a persistent update entry

Status: accepted for owner visual review, 2026-10-12

## Context

The owner found that 0.8.0's horizontal tab labels and headers consumed too much space in both
the root caption row and nested tab rows (WM-18). The same release made About and updates hard to
find (WM-19). Root tabs replace the caption toolbar, which held the only Help menu. The update
controller, About window and installer still worked, but their visible entry vanished.

## Decision

Use compact 30 DIP horizontal tab headers, 11 DIP titles and 8 DIP horizontal padding by default.
Keep pin and close controls at 24 DIP, or 36 physical pixels at 150% scaling. Preserve the 40 DIP
caption and the existing vertical tab controls. A density setting remains optional and is not
needed to address the reported default.

Put one Help button in the persistent caption column beside the window controls. It opens a menu
with About, release notes and an explicit Check for updates command. A small accent badge appears
when an update is available or staged for restart. The existing About window owns the check,
download and restart flow.

## What failed

Keeping Help only in the toolbar worked in ordinary layouts but failed as soon as root tabs took
the toolbar's place. Merely adding another palette command would still leave the feature hidden.
Shrinking the entire caption or the pin and close targets would make the new density cost pointer
accessibility, especially at 150% scaling.

## Verification

Headless tests assert the command path and badge under caption tabs, the compact title and header
sizes, and the 24 DIP secondary action targets. Native dark and light screenshots at 150% are
requested for the owner's visual check on WM-18 and WM-19. The existing update controller and
installer script tests exercise check, stage, restart and replacement without touching an owner's
installation.
