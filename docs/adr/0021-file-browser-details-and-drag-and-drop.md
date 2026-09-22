# ADR 0021 — File browser: details, drag and drop, and saying where it is

- **Status:** accepted
- **Date:** 2026-09-23
- **Spike:** none

## Context

The file browser worked, and looking at it did not tell you much. Asked on 2026-09-23, with a
screenshot: can I drag and drop files, why are there no proper icons, why are there no file details,
and — pointing at two panes side by side — which one is local, which is a server, and what server?

Each question pointed at a real gap:

- **No drag and drop at all.** Copying between panes worked only through cut/copy/paste, one item at
  a time, because the list selected one row.
- **No icons, no details.** A 📁 emoji for folders, nothing for files, and no size, date or type.
- **No identity.** A pane inside a split has no tab strip, so its title never shows. A local folder
  and an SFTP server looked the same. A refused password showed as three run-on sentences in the
  status line — the model reported the same refusal for the saved directory, the fallback and the
  selection — and the only way forward was to close the pane.

## Decision

**Drag and drop between any two panes, whatever their filesystems.** A drag carries a
`FileBrowserDragPayload` — the filesystem *and* the paths — in an in-process `DataFormat`, and a drop
runs the same `FileBrowserModel.Transfer` that paste uses (ADR 0020's addendum). So local ↔ SFTP,
FTP ↔ local, server ↔ server and anything on an SMB share (a local path, ADR 0019) all work, with the
same rules: nothing is overwritten, and a move removes the original only after a complete copy.

- The effect follows Explorer: within one filesystem a drag moves, across two it copies; Ctrl forces
  a copy and Shift a move. A drop from Explorer copies unless Shift is held, because WinMux cannot
  tell whether the source is on the same volume the way Explorer can.
- Dropping on a folder row puts the items in that folder; the row lights up while the drag is over it,
  and the pane is outlined when the drop would land in the pane itself.
- **From Explorer in:** files dragged from Explorer or the desktop can be dropped on any pane,
  including a server — an upload.
- **Out to Explorer:** a local pane's drag also carries the files as files, so they can be dropped on
  the desktop or an Explorer window. **A remote pane's cannot**: Explorer would need its virtual-file
  protocol (`CFSTR_FILEDESCRIPTOR`/`CFSTR_FILECONTENTS`), which asks for a file's bytes when it is
  dropped, and Avalonia offers no way to supply data late. Downloading before the drag starts would
  make every drag of a large file a wait. Remote → local goes through a local pane instead.

**Multiple selection.** Ctrl and Shift click, as in Explorer. Cut, copy, paste, delete and drag act on
all of it; rename acts on one. The clipboard holds a list, and a cut that partly fails keeps only
what did not arrive, so trying again finishes the job rather than repeating it. Pressing on a row
that is already part of a multiple selection does not collapse the selection until the button is
released without a drag — otherwise dragging several items would be impossible.

**Icons and types from the shell.** `IFileIconSource` (`WinMux.Platform`, implemented in
`WinMux.Platform.Win32`) asks `SHGetFileInfo` with `SHGFI_USEFILEATTRIBUTES`: the icon and type name
come from the name alone, so a file on an SFTP server looks like the same file on disk. A local
program, shortcut or `.ico` is asked about by path, because only the file knows its own picture; a
network path is not, because that is a round trip per row. Results are cached per type, and the
browser decodes each distinct PNG once.

**Details, Explorer's four.** Name, date modified, type and size. `FileBrowserNavigationItem` carries
`Size` and `Modified`, each nullable, because an FTP listing may not include them and a blank is
honest where a zero is not. Clicking a heading sorts; folders stay on top; a new date or size sort
starts newest or largest first; the sort is saved with the session. Columns give way from the right
as a pane narrows — type, then date, then size — so a thin pane still shows names.

**Say where it is.** Every file-browser pane carries a location badge: *This PC*, *Network share*, or
the protocol and account (`SFTP · alice@host:2222`), with plain FTP marked *not encrypted* in the
danger colour, since the files travel in clear text too, not only the password. A remote pane
connects before it builds its model, and a failure is one sentence in a banner with **Try again**,
which asks for the password again rather than reusing the one that was just refused.

## Consequences

- The contract a pane kind must meet did not change; all of this is inside the file-browser provider,
  plus one platform interface.
- `IFileIconSource` joins `WinMux.Platform`. The shell still declares no `DllImport`.
- Remote → Explorer is the one direction that does not work, and it is documented rather than
  half-done.

## What failed

- **The headless test app had never been installed.** `WinMux.Shell.Tests` had no
  `[assembly: AvaloniaTestApplication]`, so every headless test ran in a bare `Application` with no
  theme and therefore no control templates: a `ListBox` held its items and never realised a row. The
  class comment said the Fluent theme was installed; it never was. Found when a drop on a folder row
  could not find the row. The attribute is in; the existing headless tests still pass under the theme.
- **The first verification sign-in "failed"** because the script typed the password before the
  dialog had focus. It looked like a product fault and was the harness again — the fourth time that
  pattern has appeared (CLAUDE.md section 7). The driver now waits for the dialog to be active.
- **Status wording.** "Copied 2 of 2 items here (2 file(s))" said the same number three times. A
  complete batch now says how many; "2 of 3" appears only when something is missing.
