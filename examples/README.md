# Example session files

Session files in the format decided by [ADR 0006](../docs/adr/0006-session-file-format.md).
Every `.toml` here is loaded by `ExampleSessionsTests` on each build, so an example that stops
parsing fails the suite rather than being found by a user.

## `cmd-and-explorer.toml`

`cmd` and File Explorer side by side, both opened on the same directory.

```
┌──────────────────────┬──────────────────────┐
│                      │                      │
│         cmd          │       Explorer       │
│    C:\work\winmux    │    C:\work\winmux    │
│                      │                      │
└──────────────────────┴──────────────────────┘
              a vertical divider
```

To use it, change the two `cwd` values and Explorer's `args` to the directory you want — that is
the whole edit. The pane ids are placeholders (`1111…`, `2222…`) and any unique values work.

### Things in this file that are not arbitrary

- **`direction = 'columns'`** puts the panes side by side, which is what a *vertical divider*
  means. Change it to `'rows'` for one above the other. The vocabulary avoids the
  `horizontal`/`vertical` confusion every multiplexer defines differently — see
  [ADR 0005](../docs/adr/0005-layout-engine.md).

- **`strategy = 'embed'`** for Explorer. Spike 2 measured Explorer embedding, resizing, moving and
  detaching byte-exactly, so it is one of the good cases. Apps that refuse — UWP, and anything at
  higher integrity — fall back to `'attach'` automatically
  ([ADR 0003](../docs/adr/0003-foreign-app-compatibility.md)).

- **`window_class = 'CabinetWClass'` and `window_match = 'class'`.** `explorer.exe` exits
  immediately after launching; the window belongs to the already-running shell process. Matching on
  the launched pid would never find it. `launch_delay_ms = 2000` is the measured settle time.

- **`cwd_source` differs between the two panes**, and that is correct rather than sloppy.
  `cmd` reports its directory through the OSC 9;9 prompt snippet in
  [`spikes/04-cwd/profiles/winmux.cmd`](../spikes/04-cwd/profiles/winmux.cmd), so it is
  `shell-reported` — the most trustworthy source. Explorer has no such channel, so its directory is
  only known from how it was launched: `launch-directory`. On restore the two are not weighted
  equally ([ADR 0004](../docs/adr/0004-cwd-capture.md)).

### Looking at it

```
dotnet publish WinMux.Cli -c Release -r win-x64 --self-contained false -o dist
.\dist\winmux.exe show examples\cmd-and-explorer.toml
```

`winmux show` draws the layout using the real `Layouter`, so what you see is what the layout engine
computed rather than a separate picture of what it was meant to compute. `winmux validate` parses
and reports, exiting non-zero with the reason.

### Caveat

**Nothing runs these panes yet.** `winmux show` inspects the file; there is no shell, no pty and no
window hosting. This is a format example, not a working configuration.
