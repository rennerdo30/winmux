# ADR 0020 — SFTP and FTP: implement them, because nothing else will

**Date:** 2026-09-16
**Status:** Accepted.
**Completes:** [ADR 0019](0019-network-filesystems.md), which deferred these deliberately.
**Supply-chain precedent:** [ADR 0018](0018-terminal-emulation-supply-chain.md).

## Context

[ADR 0019](0019-network-filesystems.md) decided that WinMux ships no SMB or NFS client, because
Windows already speaks both and a share is then an ordinary path. It drew an explicit line at the
same time: *"SFTP, FTP and SCP are explicitly **not** covered by this decision. Windows has nothing
to delegate to there, so the same reasoning points the other way."*

This is that other way. The asymmetry is the whole argument: delegating is right when the OS has a
tested implementation and wrong when it does not, and Windows has no SFTP or FTP filesystem client
to mount with.

The seam was already in place. `IFileBrowserFileSystem` was widened to carry write operations when
local file transfer landed, and `ICredentialStore` exists to hold passwords outside any file WinMux
owns. Neither was built speculatively for this — both had local reasons — but both are what made
this a matter of writing one class per protocol rather than a redesign.

## Decision

**Two dependencies, both MIT, both long-established:**

| | Downloads | Maintainers | Repository |
|---|---|---|---|
| `SSH.NET` 2026.0.0 | ~336M | four | public |
| `FluentFTP` 54.2.1 | ~59M | one | public |

Named and costed rather than assumed, because ADR 0018 is about what it costs when that is skipped.
Neither resembles the `Terminal.Emulation` case: both have public source, years of history and
download counts in the tens of millions. Both are hash-pinned by `packages.lock.json` like
everything else.

**Each protocol is one `IFileBrowserFileSystem`.** Nothing above that interface changed — not the
model, not the clipboard, not a single operation. That is the interface earning its keep: a server
on another continent behaves like a folder, and the non-overwriting rule, the collision naming and
the session round trip all apply without knowing where they are.

**A connection is a profile** (`ProfileKind.Sftp`, `ProfileKind.Ftp`), so it appears in every surface
that opens a pane at once — CLAUDE.md section 5a. Unlike SSH and RDP, these resolve to no command
line, so `RemoteConnection` gained `LaunchesProgram` alongside `IsConnection`.

**SCP gets no kind of its own.** It is a copy command with no directory listing, so there is nothing
for a browser to show, and every server that speaks it speaks SFTP.

### What the protocols cannot promise

Three consequences that are the protocol's, not the implementation's, and are surfaced rather than
papered over:

- **No Recycle Bin.** `CanRecoverDeletes` is false, so `Delete` refuses and says why;
  `Shift+Delete` deletes after confirming. A hidden `.winmux-trash` directory was considered and
  rejected: it is a promise WinMux could not keep the first time anyone touched the server by other
  means.
- **No server-side copy.** Duplicating a remote file costs its size twice over the wire, because the
  bytes come to the client and go back. There is no client-side fix.
- **FTP is clear text.** The client asks for FTPS (`FtpEncryptionMode.Auto`) and uses it wherever
  the server offers it. A server with no encryption still connects, because those are exactly the
  devices that have nothing else — and the sign-in dialog says so in red. Refusing would make WinMux
  unable to reach them at all; connecting silently would be worse than either.

### Paths

`RemotePath` does POSIX path arithmetic and never delegates to `System.IO.Path`. On Windows that
type answers for Windows: `Path.Combine("/srv", "logs")` gives `/srv\logs`, and `Path.GetFullPath`
would anchor a remote path onto the local current directory. Both produce a plausible string that
reaches the server and creates a directory with a backslash in its name.

## Consequences

- **Verified against a real server**, which is the only thing that proves the library calls are the
  right calls — a wrong one compiles. SFTPGo in portable mode serves both protocols from one
  throwaway binary and needs no installation, so this costs a download rather than a test rig.
  `RemoteLiveTests` covers listing, create, rename, recursive copy and recursive delete for both,
  plus the model end to end; it skips visibly when `WINMUX_TEST_SFTP`/`WINMUX_TEST_FTP` are unset,
  so CI stays green without pretending to have tested anything. The full path was then driven
  through the UI: a session file, the credential dialog, a listing, and a folder created on the
  server.
- `Xunit.SkippableFact` joins the test project. xunit 2.x has no `Assert.Skip` — that is v3 — and a
  test that silently passes when its server is absent is a green tick proving nothing.
- **A password never reaches the session file.** The descriptor carries scheme, host, port, user and
  (for a key) the key's *path*; the secret lives in Windows Credential Manager, and a test asserts
  the exact set of keys written so that adding one later has to be deliberate.
- **SFTP can authenticate with a key**, using the `Identity` field the profile already had for SSH.
  A key connection prompts for nothing.

## What failed

**`LaunchProfile.Equals` had been silently dropping edits since connections were added.** It listed
every field except `Host`, `Port`, `User` and `Identity` — so changing a saved host's address
compared equal to the old profile, and the settings UI, which asks "did this change?" before
writing, discarded the edit. This is the *exact* failure the comment above that method was written
to warn about, reintroduced one session later by adding fields and not the comparisons. Found by
reading the type while adding two more kinds to it, not by any test. There are tests now.

The lesson generalises past this method: a hand-written `Equals` on a record is a standing
invitation to this bug, because the compiler stops helping the moment it exists. Every field added
to `LaunchProfile` has to be added in three places, and nothing enforces it but a test.

## Addendum, 2026-09-23 — copying between filesystems

**Context.** Every operation was within one filesystem. The shared clipboard held only a *path*, so
cutting `/home/alice/notes.txt` in an SFTP pane and pasting in a local one handed that path to the
local disk — a failure, not data loss, but the feature that makes a remote pane worth having beside a
local one did not exist.

**Decision.**

- The clipboard remembers **which filesystem** its path belongs to (`FileBrowserClipboard.FileSystem`).
  Same instance: the filesystem copies or moves for itself, exactly as before. Different instance:
  `FileBrowserTransfer` streams it.
- `IFileBrowserFileSystem` gains `ReadFile` and `WriteNewFile`, **callbacks rather than returned
  streams**. Each remote filesystem serialises its connection behind a lock, and a stream handed out
  of that lock would be read while the owning pane used the same connection for something else.
- `WriteNewFile` **never overwrites**: `FileMode.CreateNew` locally, `SSH_FXF_EXCL` over SFTP. FTP has
  no exclusive create, so it checks first under the connection lock — the most the protocol allows.
- **A file that was not completely written is removed**, and only a file this transfer created: if the
  destination name was taken, the file there is somebody else's.
- **A move is a complete copy, then removing the original** — to the Recycle Bin where the source has
  one. A move interrupted halfway leaves the source whole. A failure to remove the original is
  reported ("it is still there") without failing the paste, because the copy is what mattered.
- **One transfer at a time, across all panes.** A transfer holds the source connection's lock and the
  destination's. Two opposite transfers between the same two servers would take them in opposite
  orders and deadlock; a global semaphore removes the cycle, and parallel transfers over one link are
  not faster anyway.
- **A 64-level depth limit**, because an SFTP symlink to its own ancestor lists as an ordinary
  directory and would otherwise be copied until the disk filled.
- **Other panes are told** (`FileBrowserChanges`). A pane re-read its own directory after its own
  operations, but never saw another pane change it — so after cut-left, paste-right, the left pane went
  on listing a file that had gone. Matched on filesystem instance *and* path: `/data` on two servers
  is two directories.
- Closing a remote pane now disposes its connection **off the UI thread**. Disposing takes the
  connection lock, which a transfer from another pane may be holding for minutes — waiting for it on
  the UI thread would freeze the shell (priority 2).

**Verified** with unit tests against an in-memory POSIX filesystem (failure midway, cancellation, a name
Windows cannot hold, a closed source pane, a self-referencing link, each guard mutation-tested), with
`RemoteLiveTests` round-tripping a 300 KB binary and a nested folder through SFTPGo on both protocols
byte for byte, and on screen: server → local copy (hash identical), local → server move, and the
source pane updating itself.

### What failed

- **Clicking empty space in a file-browser pane did not focus it.** Found on screen: a click below the
  last row, then `Ctrl+V`, pasted into the *other* pane. Fluent's `ListBox` is not focusable — only its
  rows are — so `_list.Focus()` returns false and does nothing. That call was also the runtime's
  `IPaneRuntime.Focus()`, so moving to a file-browser pane from the keyboard had the same silent fault
  whenever no row already held focus. Now `FocusList()` focuses the selected row, else the first, else
  the list; a headless test clicks empty space and was verified by removing the handler.
- **The first version of that headless test passed alone and failed in the suite**, because it listed
  the shared temp folder, which the rest of the suite fills while running, so a row sat under the
  pointer. It now gets its own empty directory.
- **A cancelled file operation never said so.** `Perform` set "It was cancelled" and then called
  `Refresh`, which reset the status line. Found by the cancellation test for this feature; present
  since the file operations shipped.
