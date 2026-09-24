# ADR 0025 — Connection folders, inheritance, and other tools' session files

- **Status:** accepted
- **Date:** 2026-09-24
- **Spike:** none

## Context

Asked for: import sessions from MobaXterm, WinSCP, FileZilla, PuTTY, mRemoteNG and Remote Desktop
Connection Manager — "also maybe get some inspiration how they handle hierarchical rdp settings".

Three questions had to be answered before any parsing, and the answers shape everything:

1. **Do profiles gain folders and inheritance?** — *"lets also do the inheritance properly! i want
   us to nicely support remote connections!"*
2. **What happens to stored passwords?** — *"depends on the tool. decrypt and store in our crypt
   store when possible."*
3. **Import once, or keep using the original?** — *"no real import. we read and edit them, so they
   can still be used in their original tool. but the user can import them with a click of a button
   to be in our config format."*

The third is the load-bearing one. WinMux is not a converter that runs once; it is a **client** of
other tools' session files, reading and writing them in place so that PuTTY and mRemoteNG go on
working on the same data. Converting to WinMux's own format is an action the user takes, not a
precondition for seeing their connections.

What the existing model cannot express: `LaunchProfile` is a flat list. A connection in mRemoteNG or
RDCMan lives in a folder and takes its username, domain, gateway, resolution, colour depth and
credentials from that folder unless it says otherwise — which is the entire reason people with two
hundred servers use those tools rather than a list.

## Decision

### Their own assembly, not Core

`WinMux.Connections`. Core is the layout tree, the session model and the config; six foreign-format
parsers are none of those, and mRemoteNG's AES-GCM needs a crypto library Core has no business
carrying.

This was not the original plan — the first sources went into Core, and `CoreIsPlatformFreeTests`
failed the build the moment a package arrived with them, with a message saying that adding one is a
design decision belonging in an ADR. It was right. The guard existed to make exactly this call
deliberate rather than incidental, and it did.

### A connection tree, separate from profiles

`WinMux.Core/Connections` gains a tree of `ConnectionFolder` and `ConnectionEntry`. It is **not** a
replacement for `LaunchProfile`: a profile answers "what can I put in a pane", a connection answers
"which machine, with which settings, inheriting what from where". An entry becomes a profile at the
moment it is opened.

Every setting is `Inherited<T>`: either a value this node states, or the absence of one. Resolution
walks to the root and takes the first node that states it, which is what every tool named above
does and what makes a folder worth having. A resolved value remembers **which node supplied it**, so
the UI can say "domain: CORP (from *Production*)" rather than showing a value the user cannot find.

### Sources are read *and* written

`IConnectionSource` reads a tree and writes changes back. The rules that matter more than the
parsing:

- **Preserve what we do not understand.** A source keeps the original document and edits it, so a
  MobaXterm setting WinMux has never heard of survives a save. A parser that round-trips only the
  fields it knows would quietly destroy the rest of the user's configuration.
- **Never the only copy.** The first write to a file makes a `.winmux-backup` beside it.
- **Refuse rather than guess.** A file that does not parse is reported, not skipped and not
  rewritten.

### Secrets, per format

The rule from CLAUDE.md stands: **no password is written to any file WinMux owns.** Reading someone
else's file is a different act from writing one, and each format gets the honest answer rather than
a uniform one.

| Tool | Stored as | WinMux |
|---|---|---|
| PuTTY | not stored | nothing to read |
| FileZilla | base64, in plain sight | read; offer to move to Credential Manager |
| WinSCP | reversible obfuscation, no secret | read; offer to move |
| MobaXterm | reversible, machine-bound (DPAPI) | read; offer to move |
| mRemoteNG | AES-GCM from a password, default `mR3m` | read **only after asking for it** |
| RDCMan | DPAPI, bound to the Windows account | read; cannot be moved to another machine |

"Offer to move" means exactly that: the credential goes to Windows Credential Manager, and the
foreign file is left as it was unless the user asks for it to be cleared. WinMux does not silently
harvest a password store.

A decrypted secret never reaches the connection tree in memory as a field. It is fetched through
`ICredentialStore` at the moment of connecting, as SFTP and FTP already do (ADR 0020).

### Converting is one action

"Import to WinMux" copies the subtree into WinMux's own connections file and stops reading the
foreign one for those entries. It is reversible only by doing it again the other way, so it asks
first and says what it will copy.

## Consequences

- Two models that look similar — profiles and connections — with a documented reason. Expect this
  to be the first thing a future session wants to "simplify", and the first table above is why it
  should not.
- Writing to a running tool's file is a real hazard: PuTTY keeps sessions in the registry and reads
  them on demand, but mRemoteNG holds `confCons.xml` open and writes it on exit. A source declares
  whether it is safe to write while the other tool is running, and the UI refuses rather than
  racing.
- Six formats is six parsers, each with a round-trip test against a real sample file. They are
  independent, so they can land one at a time.

## Status of the work

**All six sources are implemented.** Each has a round-trip test against a sample in that tool's own
shape, and each password routine is checked against an encoder written from the format rather than
against itself — a decoder that only agrees with its own mistakes would otherwise pass.

| Source | Hierarchy | Writes | Secrets |
|---|---|---|---|
| FileZilla | folders | yes | base64, read on request |
| PuTTY | none, and does not invent any | yes | none stored |
| WinSCP | in the section name | yes | obfuscation, decoded |
| MobaXterm | `SubRep` path | yes | not in this file |
| mRemoteNG | containers, field-by-field inheritance | yes | AES-GCM, default key or one supplied |
| RDCMan | groups, block-by-block inheritance | **no** | DPAPI, this account only |

Two things fell out of building them that were not obvious when this was written:

- **A credential belongs to a node, not to a host.** mRemoteNG and RDCMan both state one credential
  on a folder and inherit it across everything under it, so a scan that looked only at connections
  would find nothing at all in a well-organised file.
- **RDCMan is read-only.** It is retired, undocumented, and its files carry elements whose meaning
  is only inferred. Reading a format well enough to show it is not the same as knowing enough to
  rewrite somebody's server list, and the difference is somebody's server list.

**Not started:** the UI — a connections panel, the resolved-value display, and the one-click
convert.
