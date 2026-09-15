# HANDOFF — WinMux

*A rolling snapshot, not a changelog. Rewrite it; never append to it. See CLAUDE.md section 10.*

## Where we are

**Phases 0–5 are complete. Phase 6 — the GUI — is most of the way through its first pass.**

WinMux runs terminal panes, foreign-application panes, a file browser and empty panes, all as
providers behind `WinMux.Panes`. The platform layer is extracted, so `WinMux.Shell` declares zero
`DllImport` ([ADR 0013](docs/adr/0013-phase-5-platform-layer.md)). Tab groups nest anywhere with a
strip on any edge ([ADR 0014](docs/adr/0014-nested-tab-groups-and-shell-chrome.md)). Any installed
application can be put in a pane through a Start Menu picker, kept as a profile, or adopted from a
window that is already open ([ADR 0015](docs/adr/0015-profiles-app-catalog-and-empty-panes.md)).
Panes can be renamed — double-click a tab, right-click it, or `Ctrl+B ,` as in tmux — and the name
outranks whatever the program inside calls itself. Terminals scroll back through their history and
support mouse selection with copy. The window wears its own Windows 11 caption: the toolbar lives
*in* the title bar next to the app icon, with minimise, maximise and close drawn by us
([ADR 0016](docs/adr/0016-windows-11-chrome.md)), and the chrome and dialogs are on Fluent 2's type
ramp rather than a size below it.

Gate: `dotnet build WinMux.slnx -c Release` and `dotnet test WinMux.slnx -c Release`.
**Verified 2026-09-16: 727 passed, 0 warnings.** Four more are *skipped* by design — the live
SFTP/FTP tests, which need a server and say so rather than passing quietly (ADR 0020). Version 0.6.0.
Everything through local file operations is **pushed** (`4bc56ba`); the SFTP/FTP work described
below is the only thing newer than `origin/main`.

Run it: `run.cmd`, or `scripts/run.ps1 -Session examples/tabs-and-splits.toml`.
Package it: `publish.cmd` → `dist/WinMux-0.6.0-win-x64/` and a zip.

## What just happened

**2026-09-15**, one long session. `git log 68103d5..` is the changelog; this is what a newcomer
needs to know happened, and where the reasoning lives.

- **Phases 4 and 5 closed.** Pane providers and the file browser (ADR 0012); the platform layer
  extracted so the shell declares zero `DllImport` (ADR 0013).
- **Phase 6, the GUI.** Nested tab groups and shell chrome (ADR 0014), profiles and the app
  catalogue (ADR 0015), then a Windows 11 pass over the chrome, the dialogs and the pane layer
  (ADR 0016): Fluent 2 metrics, a caption the app draws itself with snap layouts claimed back,
  inset rounded panes, an accent focus ring in the gutter, and terminal output rendered in colour
  for the first time.
- **Everything on the feature list landed**: scrollback search, tab reordering by key, menu and
  drag, focus reconciliation against the OS, dragging a running window into a pane, and pane
  providers loaded from `providers/` beside the executable with a working sample.
- **Two long-standing claims were finally measured, and one was wrong.** Input-queue attachment
  does *not* starve the shell of input — it blocks *focus* for the duration of a wedge, 201 ms
  against 4,386 ms (ADR 0017). Symbol key bindings never worked on this machine at all, because
  `KeyStroke`'s table assumes a US ANSI keyboard and the hardware here is Japanese 106/109.
- **Three separate times, a measurement harness produced the alarming result rather than the
  product.** That is the most transferable thing this session produced; see *Do not re-do* and
  CLAUDE.md section 7.
- **Dependencies are hash-locked** and `Terminal.Emulation` reassessed (ADR 0018).

**2026-09-16.** CI, a documentation site and an in-app updater, all following the shape used in
`bifrost-proxy`:

- **Three workflows.** `ci.yml` builds, tests and packages on Windows with `--locked-mode` and
  `-warnaserror`; `docs.yml` deploys the site to Pages; `release.yml` publishes a tagged build with
  `checksums.txt`. The repository had no CI at all before this.
- **Docs site** at `docs/` — Astro + Starlight, published to
  <https://rennerdo30.github.io/winmux/>. The ADRs stay in `docs/adr/` and are mirrored into the
  site at build time by `docs/scripts/sync-adrs.mjs`, so there is one editable copy of each
  decision and the GitHub links keep working.
- **In-app updater.** Checks GitHub a few seconds after launch, offers what it finds, and installs
  nothing without being asked. It refuses any archive whose SHA-256 is not in the release's
  `checksums.txt`. Windows locks a running image, so it stages beside the install, saves the
  session, and hands the swap to a script. `Help` in the toolbar links the docs, the releases and
  the updater.
- **The terminal measures its own font** instead of assuming an 8.45px cell, and the family and
  size are settings. The old constant was wrong for both the default face and the fallback, so
  every *position* in a row drifted away from its glyphs.
- **SSH and Remote Desktop are profiles**, not a new pane kind — one list behind every surface that
  opens a pane (CLAUDE.md section 5a). `ICredentialStore` landed with them, backed by Windows
  Credential Manager, so that no password ever reaches a file WinMux owns.
- **The file browser can change files, not just look at them.** New folder, rename, cut/copy/paste
  and delete, each with a key, a context-menu entry and — for the common ones — a button. Nothing
  overwrites: a name collision becomes `report (2).txt`, so a mistake never costs the original.
  Delete goes to the Recycle Bin through `IFileTrash`, and a *failed* recycle is never quietly
  upgraded to a permanent delete.
- **SFTP and FTP are built in** ([ADR 0020](docs/adr/0020-sftp-and-ftp.md)), because unlike SMB
  there is no Windows client to delegate to. Each is one `IFileBrowserFileSystem` — SSH.NET and
  FluentFTP, both MIT — and *nothing above that interface changed*. A saved connection is a profile
  like any other, the password goes to Credential Manager and never to the session file, and SFTP
  can use a key instead. **Verified against a real server** (SFTPGo portable, both protocols) and
  then through the UI: session file, credential dialog, listing, folder created on the server.
- **Network shares are Windows' job, and that is now decided and written down.** WinMux adds no SMB
  or NFS client: Windows mounts a share, WinMux browses the path. An SMBLibrary dependency was
  costed (LGPL-3.0 is compatible with MIT — weak copyleft, linking does not relicense us) and then
  **dropped as unnecessary**. The guide is
  [Network shares](docs/src/content/docs/network-shares.mdx).

## The next action

**Use it for an hour, then write down what annoyed you.** Everything on the feature list is done and
was seen working on screen; what is left is the class of finding that only comes from use, and this
project has now had two sessions of code-reading produce less than one screenshot did.

The connection work asked for on 2026-09-16 — RDP, SSH, FTP, SCP, SMB, NFS — is now **complete**:
SSH and RDP delegate to the Windows clients, SMB and NFS to Windows itself (ADR 0019), SFTP and FTP
are implemented (ADR 0020), and SCP is deliberately absent because it cannot list a directory.

The obvious next piece is **copying between two filesystems** — dragging from an SFTP pane onto a
local one. Every operation today is within one filesystem; the interface already has the streams
this would need.

The two things still queued are taste calls rather than gaps, both from the 2026-09-15 critique:
the toolbar is uniform icon+label with a divider after every group, where Explorer's command bar has
neither; and `LayoutMetrics` is already parameterised, so a Normal/Compact density setting is a
settings card rather than an architecture change. Neither should be done without deciding it is
wanted.

## Waiting on you

Three things, and only three. Each needs a person, a machine setting or a judgement — none is
unfinished engineering, and nothing in the codebase is waiting on them.

- **Run the mixed-DPI check.** Set one display to a different scale, then
  `scripts/verify-mixed-dpi.ps1`. It refuses to report anything while the scales match, launches
  WinMux across the seam, and lists what to look at. This is the last measurement in the project
  that has never been taken, and it needs two monitors at different scales — which this machine has
  never had.
- **Decide `Terminal.Emulation`.** [ADR 0018](docs/adr/0018-terminal-emulation-supply-chain.md)
  has the facts, four costed options and a recommendation: ask the author to publish the source,
  keep the hashes pinned, and benchmark `tomlm/Iciclecreek.Avalonia.Terminal` before assuming a
  replacement is expensive. Nobody has asked the author; that is a message, not a commit.
- **Use it for an hour.** See *The next action*.

## Standing constraints

**These are not open work.** They are decided, implemented and permanent, and they live here so
that a future session recognises them as answers rather than rediscovering them as questions.

- **Higher-integrity attach is constrained by UIPI**, and always will be. A non-elevated process
  cannot manipulate an elevated application's windows. Failures are detected and explained in plain
  words, and **WinMux will not ship elevated to work around it** (CLAUDE.md section 5).
- **WinMux speaks no network filesystem protocol, and will not**
  ([ADR 0019](docs/adr/0019-network-filesystems.md)). Windows mounts SMB natively and
  NFS through an optional component; a share is then an ordinary path and the file browser walks it.
  A built-in client would reimplement Kerberos, DFS and offline files, and would parse untrusted
  network bytes inside the WinMux process, for no capability gained. SFTP and FTP are *not* covered
  by this — Windows has nothing to delegate to there, so they remain real work that is wanted.
- **An X11 port needs its own host executable, not a shim.** `WinMux.PaneHost` keeps its 35 imports
  deliberately; reparenting *is* a platform implementation (ADR 0013, decision 5). This is the
  design, not a gap in it.

## Do not re-do

**Architecture and process model**
- Do not hand-roll ConPTY (ADR 0002) or `SetParent` into an Avalonia window — the latter succeeds,
  reports the right parent and rect, and paints nothing (ADR 0007).
- Do not call a foreign HWND from the UI thread, and never hard-kill a host that still owns a
  child (ADR 0008, ADR 0011).
- Do not add a method to `WinMux.Platform` before something calls it (ADR 0013).

**Performance**
- Do not put `SnapshotProcesses` on an interactive path: 123 ms for ~330 processes against 0.12 ms
  for the working-directory read it enables. `CachedProcessInspector` shares one list.
- Do not call `SessionController.RequestSave()` from anything firing at input frequency. Debouncing
  the *write* does not help when producing the snapshot is the expensive part.

**Avalonia and chrome**
- Do not write `Rect`, `TabStrip` or `Path` unqualified in a file importing Avalonia; assume any
  short geometric or widget-shaped name in Core has an Avalonia namesake.
- Do not set `Background` directly on a Button — it replaces what Fluent's template animates and
  silently kills hover and pressed feedback. Style the templated `ContentPresenter`.
- Do not trust "it lays out correctly but paints nothing" without a DPI-aware screenshot — see
  *Build and tooling* below. The earlier note here, that a right-aligned toolbar group paints
  nothing and the reason is unknown, was very likely that measurement error rather than an Avalonia
  bug. The buttons still sit in the main `StackPanel`; moving them back is untested but should work.
- Do not left-align a child inside a Grid star column and expect it to be clipped to that column.
  `HorizontalAlignment.Left` makes a control take its *desired* width instead of the width it was
  given, and a Grid does not clip children, so it draws straight over the next column. Let it fill
  the column and give the overflow to a ScrollViewer.
- Do not draw a title bar while Avalonia is also drawing one. Avalonia 12 replaced 11's chrome-hints
  enum with `Window.WindowDecorations` (`Full` / `BorderOnly` / `None`) and
  `Avalonia.Controls.Chrome.WindowDrawnDecorations`. `BorderOnly` is the setting that leaves the
  border, shadow and resize grips to Avalonia and the caption to the app; at the default `Full` the
  window title paints twice, overlapping itself.
- Do not leave `ThemeVariant.Default` and assume the platform is followed; read
  `IPlatformSettings.GetColorValues()` and set Dark or Light explicitly.
- Do not reuse the horizontal tab-strip thickness for a vertical strip (28px of titles is a column
  of ellipses); `LayoutMetrics` carries both.
- Do not put palette or modal chrome over the pane canvas; native windows paint above it.

**Build and tooling**
- Do not `dotnet build` a single x64 project then run `bin/x64/Release/...` — a bare project build
  lands in `bin/Release/` and you test a stale exe. Build the solution.
- Do not guess another project's output path in a copy target; ask MSBuild with `GetTargetPath`.
- Do not run `scripts/phase*-demo.ps1` under Windows PowerShell 5.1; the verifiers need pwsh 7.
- **Do not screenshot the app from a DPI-unaware process.** PowerShell is one. On this 150% display
  every coordinate it sees is virtualised, so `GetWindowRect` reported 1500x900 for a window that
  was really 2250x1350 and `CopyFromScreen` captured the wrong region — which looked exactly like
  the right-hand end of the chrome failing to render, through several rounds of investigation.
  Call `SetProcessDpiAwarenessContext(-4)` first. The tell that it is the harness and not the app:
  render the control in-process to a `RenderTargetBitmap` and compare.
- Do not resize an Avalonia window with `SetWindowPos` from outside it. Avalonia goes on laying out
  at the size it believes it has, and everything past that width falls outside the real window.
- Do not rasterise an SVG with cairosvg, rlPyCairo or svglib+renderPM — all need a cairo Windows
  does not ship, or fail on rounded rectangles. Headless Chrome works (`assets/build-icon.py`).

**Keyboards**
- Do not assume `KeyStroke`'s symbol table describes the keyboard in front of you. It is US ANSI,
  and this machine is Japanese 106/109: `:` unshifted, `"` on Shift+2. `VkKeyScanEx` answers the
  question for every installed layout in a few lines of PowerShell — do that before theorising
  about why a symbol binding does not fire.

**Measuring, not assuming**
- Do not hardcode a character cell size. The renderer used `CellWidth = 8.45` against a font whose
  real advance is 8.203 (Cascadia Mono) or 7.697 (Consolas, the fallback and the common case), so
  every *position* — run origins, cursor, selection, search highlights, coloured backgrounds —
  drifted from the glyphs across a row. `TerminalFontMetrics` measures the face in use.
- Do not add a property to `WinMuxSettings` without adding it to `SettingsFile` as well. The file is
  hand-written rather than serialised from the type, so a forgotten property compiles and silently
  resets on every launch; that happened twice before `SettingsRoundTripTests` existed to catch it.

**Avalonia threading and focus**
- Do not touch a control inside the lambda handed to `RunNavigationAsync` — it runs on a background
  thread via `Task.Run`, and reading `TextBox.Text` there throws "the calling thread cannot access
  this object". Read the control on the UI thread and capture the value. The file browser's address
  bar did this from Phase 4 and **typing a path and pressing Enter never worked once**; nothing
  caught it because no test constructs the pane.
- Do not rebuild a `ListBox`'s items without putting focus back. Clearing the items destroys the
  focused row, so the next keystroke goes nowhere — copy-then-paste from the keyboard silently did
  nothing. Restore it from a `Dispatcher.Post` at `Input` priority, not inline: the new rows are not
  laid out yet and focusing an unarranged control fails quietly.
- Do not build a `KeyGesture` with `KeyGesture.Parse`. It validates at run time and throws — `"Del"`
  is not a name it knows — and the exception escaped a provider's `CreateAsync`, so the whole pane
  became uncreatable. `new KeyGesture(Key.Delete, KeyModifiers.None)` is checked by the compiler.

**Driving the UI from a script**
- Always `AppActivate` (or click) before `SendKeys`. A key sent to an unfocused window goes to
  whatever *is* focused, which looks exactly like a feature that does not work.
- A modal left open blocks every later click and key, and produces the same silent nothing. Three
  separate "bugs" in one session were one un-dismissed dialog. Screenshot before concluding, and
  check for a dialog first.

**Network paths**
- Do not give `cmd.exe` a UNC working directory. It prints a warning nobody sees and starts in
  `C:\Windows` instead, which defeats session restore silently — a `cmd` pane saved on a share comes
  back somewhere else. PowerShell, pwsh and WSL are all fine. Measured 2026-09-16.
- Do not assume a share root behaves like a directory. `Directory.GetParent(@"\\server\share")`
  returns `null`, so "up" has to be handled as a root the way `C:\` is. `FileBrowserUncTests` pins
  this, and normalisation, and the offline-share fallback.

**Secrets**
- Do not put a password in the profiles file, or any file WinMux owns. It is plain text,
  hand-editable, copied between machines and committed by mistake. `ICredentialStore` is the
  contract and Windows Credential Manager is the implementation; a test asserts that no key written
  to the profiles file is named after a secret.
- Do not reach for `SecureString`. Microsoft documents it as not recommended for new development,
  and a .NET string cannot be zeroed anyway. The protection that is real is that the secret lives
  in the OS store and is materialised late and briefly.

**Judgement**
- Do not carry an unverified convenience claim forward as if it were a finding. "SMB already works
  via UNC paths" was asserted across several turns, written into a plan, and only checked afterwards
  — where it happened to be true. `\\localhost\C$` is reachable without elevation and makes the
  check a two-minute job on any Windows machine; there was never a reason not to do it first.
- Do not render a terminal row as one `FormattedText` with one brush. It looks correct on an
  uncoloured prompt and silently throws away every colour and attribute the engine parsed; a run
  per cell is the other wrong answer, at 12,000 text layouts a second on a wide row.
- Do not draw a focused-pane indicator inside the pane, and do not gate it on
  `IsKeyboardFocusWithin`: a native pane paints over anything inside its rectangle, and that flag is
  false exactly when a dialog or the palette has focus, which is when the question is being asked.
- Do not interrupt for success. The restore modal made the first act of every session dismissing a
  paragraph nobody reads, which is how people learn to click OK unread.
- Do not intercept unshifted PageUp in a terminal; it belongs to the program in the pane, which is
  why scrollback uses Shift+PageUp. Anything the user types must also snap the view back to the
  live screen, or typing into history looks like the terminal has frozen.
- Do not let an automatic title overwrite a name the user chose. A shell sets its console title
  constantly, so a rename without `TitleIsCustom` reverts within seconds and looks like a bug in
  the rename rather than in the title handling.
- Do not ship a capability with no interface. Foreign-app panes existed from Phase 3 with no way to
  create one; pane resizing had no handle. Both went unnoticed for months (CLAUDE.md section 5a).
- Do not judge a colour from a screenshot: an active tab that looked light-on-dark measured
  `#3A3A3A`, exactly right. Sample the pixel or dump the resolved values.
- Do not assert exact requested rectangles for arbitrary apps; DPI rounding and minimum sizes are
  measured counterexamples (ADR 0003).
- Do not rank every OSC report over a newer deepest-child PEB, or rely on Porta.Pty's Windows
  quoting (ADR 0010).
- Do not pass a lifetime token where an operation token belongs, and do not let action dispatch
  return before async pane creation completes (ADR 0012).

**Environment**
- The development checkout is reachable by two drive letters via subst/junction, so a tool may
  report a path that looks unfamiliar. It is the same working tree.
- Opening a session file under `examples/` rewrites it — `scripts/run.ps1` copies it aside first.
