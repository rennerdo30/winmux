# ADR 0008 — Top-level pane hosts with redirected-pipe IPC

- **Status:** accepted
- **Date:** 2026-09-12
- **Code:** `WinMux.PaneHost`, `WinMux.Shell/ForeignAppPane.cs`, `ForeignWindowTracker.cs`

## Context

ADR 0001 requires the shell to avoid every synchronous call against a foreign application's
window and forbids reparenting a pane-host window into the shell until input-queue starvation is
measured. ADR 0007's direct `NativeControlHost` embedding rendered correctly, but still left the
foreign window in the shell process's input and lifecycle boundary.

## Decision

Each foreign pane gets a `WinMux.PaneHost` process. It creates a borderless **top-level** HWND,
finds and reparents the application into that window, and performs all synchronous calls against
the application itself. The shell only positions the host HWND on its existing layout thread.

The first protocol is line-oriented redirected pipes:

- host to shell: `HOST_HWND=<n>`, then `READY=<child-hwnd>` or `ERROR=<plain words>`;
- shell to host: `DETACH` preserves the application, `CLOSE` closes the pane and application.

The child HWND is reported only so sequential launches can exclude windows already claimed by
another host. The shell never sends a window message to it.

## Consequences

- A wedged application can block its own PaneHost but cannot block the Avalonia UI thread.
- PaneHost windows remain top-level, so the unresolved chained-input-queue question stays avoided.
- Shell shutdown posts `DETACH`; explicit pane close sends `CLOSE`. Hard-kill remains forbidden.
- Redirected pipes are deliberately small. A versioned named-pipe protocol can replace them when
  richer lifecycle/focus commands are needed without changing the process boundary.

## Verification

On 2026-09-12, Character Map produced `HOST_HWND` and `READY`; `DETACH` exited PaneHost with code
0 and restored Character Map as a normal top-level window with the exact same HWND. A full shell
run then launched cmd + Character Map through separate shell/host/app processes; both shell and
host remained responsive, shell close exited PaneHost, and Character Map survived with its HWND
and title restored.

## What failed

- The first draft waited for the application window before entering the host message loop. It
  could create processes that were alive but had no enumerable, responsive top-level host window.
  Discovery now runs on a worker and adoption is posted back to the host thread.
- A first verification script used PowerShell's reserved `$Host` variable and checked the child
  HWND after closing the child. That result was discarded; the corrected verifier captured the
  restored HWND before cleanup.
- A stale draft process locked the copied executable and had no usable host HWND to close. Only
  those two windowless test processes were terminated; production lifecycle uses `DETACH`.
