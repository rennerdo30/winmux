# ADR 0026 — Optional GPU rendering with software fallback

- **Status:** accepted
- **Date:** 2026-10-07
- **Spike:** none
- **Validation:** Release build with warnings as errors passed; solution tests: 1,155 passed,
  6 skipped (live SFTP/FTP), 0 failed, on 2026-10-07.

## Context

The user requested a GPU-accelerated mode that also works on VMs with software rendering.
The terminal panes are Avalonia drawing controls backed by ConPTY, not embedded cmd/PowerShell
windows. Avalonia 12.1.2 already defaults to ANGLE GPU drawing followed by Software fallback;
WinMux previously left this implicit and offered no way to force software from Settings.

The pinned package's `Avalonia.Win32.xml` documents that fallback order, also described in the
[official RenderingMode reference](https://api-docs.avaloniaui.net/docs/P_Avalonia_Win32PlatformOptions_RenderingMode).

## Decision

- Persist the platform-neutral `RenderingPreference` in Core settings as `rendering`:
  `automatic` (default) or `software`. Old files without the key retain automatic behavior;
  invalid values produce the existing settings warning and use the default.
- Load settings before Avalonia platform initialization, not in `App.Initialize`, because that
  callback is too late to choose the graphics backend.
- `RenderingOptions` in the shell maps Automatic to `[AngleEgl, Software]` and Software to
  `[Software]`, using Avalonia's existing drawing stack. No new platform calls or dependencies.
- Settings → Appearance → Rendering exposes both choices and states that restarting is required.
  This controls the shell and its terminal drawing, not another process's rendering backend.
- The same terminal control draws in both modes. No custom GPU renderer or VT engine replacement.

## Consequences

Software can be selected for VMs and graphics-driver problems. If the app cannot open, edit
`%APPDATA%\WinMux\settings.toml` with WinMux closed and set `rendering = 'software'`.
Avalonia handles automatic initialization fallback; this does not promise recovery from every
driver crash or device loss during a session. ANGLE selection is not proof of physical GPU use:
adapter and driver behavior still determine whether drawing is hardware accelerated.

Tests cover preference parsing, invalid-value warnings, serialization, fallback order and the
actual Settings control's saved choice. The software-mode guard was mutation-tested: allowing
ANGLE in Software mode made `Software_does_not_attempt_a_GPU_backend` fail; the defect was removed.
These tests do not measure native GPU execution, VM behavior,
frame latency, visual quality or Claude Code/Codex compatibility. GPU-first was already the default,
so no performance gain is claimed from making the selection explicit. Application scrolling and
keyboard-protocol problems are separate from the backend-selection change.

## What failed

- The first build attempt used the system `dotnet`, which has SDK 10.0.401 but not the repository's
  exact pinned 10.0.400; SDK resolution refused to build. A separately installed 10.0.400 under
  `%LOCALAPPDATA%\WinMuxDev\dotnet` builds without changing `global.json` or package lock files.
- Treating a new GPU flag as evidence of faster terminal drawing would be misleading: the pinned
  Avalonia backend already requests GPU-first rendering. This change provides explicit mode
  selection and a persisted software override; performance remains unmeasured.
