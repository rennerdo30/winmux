# ADR 0019 — Network filesystems: delegate to Windows, ship no client

**Date:** 2026-09-16
**Status:** Accepted.
**Relates to:** [ADR 0012](0012-phase-4-pane-providers-and-file-browser.md) (the file browser),
[ADR 0013](0013-phase-5-platform-layer.md) (what belongs behind the platform contract).

## Context

Remote connections landed as profiles — SSH through `ssh.exe`, Remote Desktop through `mstsc.exe` —
and the obvious next question was whether the file browser should reach network storage: SMB, NFS,
and then SFTP/FTP/SCP.

The request was explicitly for SMB and NFS support **independent of Windows**: a client WinMux
carries itself, so that a share works the same way regardless of what the OS offers. That framing is
what this ADR answers, and the answer for SMB and NFS turned out to be different from the answer for
SFTP and FTP.

## What was investigated

**SMB.** [SMBLibrary](https://github.com/TalAloni/SMBLibrary) is the only credible managed SMB
client for .NET: SMB 1/2/3, actively maintained, and widely used. Its licence is **LGPL-3.0**, which
prompted a compatibility question against WinMux's MIT licence.

*LGPL-3.0 is compatible with an MIT project*, and the reasoning is worth recording because it is
routinely got wrong. LGPL is **weak copyleft**: §4 permits combining the library with a work under
terms of your choosing, provided the user can replace the library with a modified version. Linking
does not relicense WinMux. The three conditions were checked against this repository rather than
assumed:

- `PublishSingleFile` is set only on `WinMux.Cli`, not on `WinMux.Shell`, where such a client would
  live — so the DLL stays a loose, replaceable file, satisfying §4(d)(1)'s "suitable shared library
  mechanism".
- Nothing sets `PublishTrimmed` or `PublishAot`, either of which would undermine replaceability.
- `scripts/publish.ps1` already copies a LICENSE into the package, so shipping the LGPL text and
  stating the library's use is a small change.

So the obligations were real but modest: ship the licence text, say the library is used, keep it a
replaceable DLL, and never modify it (modifications *are* covered by the copyleft).

**NFS.** The managed .NET ecosystem does not have an equivalent. The best-used package has ~71K
downloads and the most complete one ~624 — neither is a foundation to put a user's files on. An
independent NFS client would mean implementing ONC RPC, portmapper, mount and NFSv3/v4 in-house.

## Decision

**WinMux ships no SMB or NFS client. Windows mounts the share; WinMux browses the path.**

It does not need one. Windows speaks SMB natively and NFS through an optional component, and a
mounted share is then an ordinary path — a UNC path or a drive letter — which the existing file
browser already walks through `Directory` and `DirectoryInfo`.

Three reasons, in order of weight:

1. **It buys no capability.** Every share reachable by SMBLibrary is already reachable through
   Windows, and through Windows it also gets Kerberos, DFS referrals, offline files, and the
   system's own credential prompt and store. An independent client would be a second, worse
   implementation of something the user's machine already does.
2. **It is a protocol parser on untrusted input, in-process.** SMB and NFS parse bytes from the
   network. Doing that inside the WinMux process is a meaningful attack surface to accept, and it
   would sit *inside* the shell — the process that priority 2 of CLAUDE.md section 1 exists to keep
   alive.
3. **The LGPL question becomes moot**, along with its ongoing obligations and the fact that some
   organisations prohibit (L)GPL dependencies outright.

**The request for independence from Windows is not met, and that is the deliberate trade.** What is
given up is a share working identically on a hypothetical non-Windows port. That port does not
exist, cross-platform is a design discipline rather than a shipping commitment (CLAUDE.md section
1.6), and every platform WinMux might reach has its own SMB and NFS support to delegate to.

**SFTP, FTP and SCP are explicitly *not* covered by this decision.** Windows has nothing to delegate
to there, so the same reasoning points the other way: they need a real client, they remain wanted,
and `ICredentialStore` now exists to hold their passwords. They belong behind
`IFileBrowserFileSystem`, which is already the file browser's seam.

> **Built on 2026-09-16** — see [ADR 0020](0020-sftp-and-ftp.md). SCP still gets no kind of its own,
> for the reason given there: it cannot list a directory.

## Consequences

- A user-facing guide ships in place of code:
  [Network shares](../src/content/docs/network-shares.mdx) covers mapping SMB, enabling and using
  the NFS client, UNC paths in panes and profiles, and the failures that actually happen.
- **The "it already works" claim was verified rather than assumed**, because it had been asserted
  several times without being checked. `FileBrowserUncTests` now pins the behaviour, using .NET's
  real UNC path logic against a fabricated tree so the tests need no share and no network.
- Two things were found in that verification that had never been considered:
  - **A share root has no parent.** `Directory.GetParent(@"\\server\share")` returns `null`, the way
    it does for `C:\`. The model already treated a null parent as a root, so it was correct by
    accident rather than by design; it is now correct on purpose and tested.
  - **`cmd.exe` cannot start in a UNC working directory.** It prints a warning and starts in
    `C:\Windows` instead. This silently defeats session restore for a `cmd` pane saved on a share,
    it is a limitation of `cmd` that nothing outside it can fix, and it is documented in the guide
    and in troubleshooting. PowerShell, pwsh and WSL are unaffected.
- WinMux still stores no password for a share, consistent with the policy for SSH and RDP. Windows
  Credential Manager holds it, where the user can inspect and delete it without trusting WinMux to.

## What failed

**An unverified claim was carried forward as if it were a finding.** "SMB already works via UNC
paths, the file browser walks `Directory` which handles it natively" was stated several times across
the session, used to shape the plan, and only checked at the end — where it happened to be true.
Had it been false, the decision above would have been made on a wrong premise and the guide would
have told users something that did not work.

The check cost two minutes and needed nothing special: `\\localhost\C$` is reachable on any Windows
machine without elevation, which makes a live UNC path always available for exactly this. The lesson
is in HANDOFF's *Do not re-do*, and the ordering is the point — verify, then plan on it.

**A shell layer silently ate backslashes** while these files were being written, turning
`\\server\share` into `\server\share` in two places before anyone looked. Both were caught by a
sweep rather than by review. Writing UNC paths through nested shell quoting is a known hazard in
this repository; use the file-writing tools directly, and sweep afterwards regardless.

## Revisiting this

Reopen it if a target platform appears with no usable native SMB client, or if delegating turns out
to block something concrete — per-share credentials that Windows cannot express, say. "It would be
more self-contained" is not a reason; it was the original one, and it did not survive costing.
