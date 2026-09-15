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
