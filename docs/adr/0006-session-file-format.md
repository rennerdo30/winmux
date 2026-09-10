# ADR 0006 — Session file format: TOML, with the layout tree flattened

- **Status:** accepted
- **Date:** 2026-09-10
- **Code:** `WinMux.Core/Session` — `SessionFile`, `TomlSessionWriter`, `TomlSessionReader`, `Flattener`
- **Closes:** CLAUDE.md §9 "Session file format: JSON or TOML? (ADR before first write)"

## Context

CLAUDE.md §4 requires the session file to be human-readable and hand-editable, versioned from the
very first write, with a migration path, and treated as user data — a failed load is a bug worth a
backup file, never a reason to silently start empty. §9 left the format open and required an ADR
before anything wrote one. Nothing had.

The user chose TOML, for readability.

## The complication

TOML is more readable than JSON for *flat* data. The layout is a **recursive tree**, which is
TOML's weak spot: encoding it as nested tables produces headers like

```toml
[[windows.root.children.children.children]]
```

which is worse than the JSON it was meant to improve on. Choosing TOML therefore forces a second
decision about *shape*, and getting that wrong would defeat the reason for choosing it.

Three shapes were considered and compared side by side on the same session: nested tables, a flat
node list with generated ids, and a tmux-style compact layout string (`cols(0.4:shell|0.6:...)`)
with flat pane tables.

## Decision

**1. TOML, via Tomlyn 2.10.1** (5.7M downloads, TOML v1.0.0, platform-neutral).

**2. The layout tree is FLATTENED into `[[windows.nodes]]` with generated ids.** Every table stays
shallow, which is what TOML is good at. Node ids (`n0`, `n1`, …) are file-local, assigned in
pre-order, and regenerated on every save — deterministically, so a session file does not churn in
a diff for no reason. The tree shape is followed through `children` id lists rather than seen at a
glance; that is the price, and it is the right one because the tree is what the *program* manages.

The compact layout string was rejected despite being the most readable at a glance: it needs a
custom grammar and parser, which is more code to get wrong for a file that has to survive a crash.

**3. Panes are flat `[[windows.panes]]` tables and are explicitly the editable part.** The realistic
hand-edit is changing a `cwd` or a `program`, not restructuring a tree, and both are one obvious
line. The file opens with a comment saying so, and saying that restore does not resurrect process
state.

**4. Strings are written in TOML's literal form (`'…'`) wherever possible**, so Windows paths keep
their backslashes instead of becoming `C:\\Program Files\\…`. The escaped basic form is a fallback
for values a literal string cannot represent.

**5. The writer is hand-rolled; the reader uses Tomlyn.** Readability was the whole point, which
means controlling alignment, comments, key order and when a table goes inline — none of which a
serializer gives you. Hand-written escaping is not trusted: a test round-trips every generated file
through Tomlyn, an independent parser, so the writer is validated by something that is not itself.

**6. Reading validates and refuses; it never guesses.** Dangling node references, leaves pointing at
absent panes, duplicate ids, unknown enum values, unreachable nodes and **cycles** are all rejected
with a message naming the offender. Cycles matter especially: a hand-edited file could contain one,
and following it would hang the app on load — a far worse failure than refusing the file.
Unreachable nodes are refused rather than ignored because silently dropping them is exactly the
"pane that vanishes without explanation" §8 forbids.

**7. Saving is atomic and a corrupt file is quarantined, not replaced.** Save writes a sibling
temporary file then replaces, so a crash mid-write cannot leave a truncated file where a working
one was. Load copies an unreadable file to `session.toml.corrupt-<timestamp>`, leaves the original
untouched, and says where the copy went.

**8. `WinMux.Core` may now declare `Tomlyn`.** ADR 0005 asserted Core had *no* dependencies; this
relaxes that to an explicit allow-list in `CoreIsPlatformFreeTests.ApprovedDependencies`, each entry
naming the ADR that admitted it. Tomlyn is platform-neutral (netstandard2.0), so the *portability*
boundary is unchanged — the ban on platform assemblies still holds unconditionally.

## Consequences

- Phase 2 can write session files. `SessionFile.Save`/`Load` is the whole public surface.
- The `Version` field is written from the first save; a future version refuses with "Upgrade
  WinMux" rather than discarding a layout it does not understand.
- Node ids are not stable identity — they are regenerated per save. Anything needing durable
  identity uses the pane `Guid`, which is persisted as-is.
- Pane ids appear as full GUIDs, which is less pretty than the mocked-up examples. Fidelity won:
  they are what actually round-trips, and `title` is there for humans.

## What failed

- **Two malformed-file tests silently tested the wrong thing.** They were built by concatenating a
  valid document with an extra `[[windows.nodes]]` block — but that block appeared *after*
  `[[windows.panes]]`, and re-opening the array there does not extend the earlier one, it replaces
  it. So the "duplicate id" fixture contained no duplicate (and threw nothing), and the "orphan
  node" fixture had lost the root instead of gaining an orphan. Both now spell out a complete
  document. A fixture assembled by string concatenation is a fixture whose content you have not read.
- **The first writer collapsed an empty `program` to absent** (`IsNullOrEmpty`), making the round
  trip lossy for a state the model distinguishes. Caught by a theory case with an empty string; now
  `is not null`.
- **Tomlyn 2.10 does not have the API the docs elsewhere describe.** There is no
  `Toml.Parse(...).ToModel()`; the route to an untyped document is
  `TomlSerializer.Deserialize<TomlTable>`. Written against the remembered API first, it did not
  compile; resolved by reflecting over the shipped assembly rather than guessing again.
