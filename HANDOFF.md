# HANDOFF — WinMux

*A rolling snapshot, not a changelog. Rewrite it; never append to it. See CLAUDE.md section 10.*

## Where we are

**Phases 0–5 are complete; Phase 6 (the GUI) has every planned feature in. 0.7.0 is released.**
What is left needs a person, a second monitor or a decision — see *Waiting on you*.

WinMux runs terminal, foreign-application, file-browser and empty panes, all as providers behind
`WinMux.Panes`; the shell declares zero `DllImport` (ADR 0013); tab groups nest anywhere (ADR 0014);
any installed app can be a profile (ADR 0015); the window wears its own Windows 11 caption (ADR 0016).
The file browser speaks SFTP and FTP (ADR 0020) and now **copies and moves between filesystems** —
server to disk, disk to server, server to server — with the same no-overwrite rule as everything else.

Gate: `dotnet build WinMux.slnx -c Release -warnaserror` and `dotnet test WinMux.slnx -c Release`.
**Verified 2026-09-23: 813 passed, 6 skipped, 0 warnings.** The 6 are the live SFTP/FTP tests, which
skip unless `WINMUX_TEST_SFTP`/`WINMUX_TEST_FTP` are set; with a server they pass too (819, 0 skipped
— checked the same day against SFTPGo 2.7.6 portable; how to run it is in `RemoteLiveTests`).
`v0.7.1` (cross-filesystem copy and the fixes found with it) was tagged and pushed on 2026-09-23;
`release.yml` builds it. `v0.7.0` was the first release (2026-09-16).

Run it: `run.cmd`, or `scripts/run.ps1 -Session examples/tabs-and-splits.toml`.
Package it: `publish.cmd` → `dist/WinMux-<version>-win-x64/` and a zip.

## What just happened

**2026-09-23 — copying between filesystems** ([ADR 0020 addendum](docs/adr/0020-sftp-and-ftp.md#addendum-2026-09-23--copying-between-filesystems)).
The clipboard now remembers which filesystem a path belongs to; a paste across two streams the bytes
through `FileBrowserTransfer`. A move removes the original only after a complete copy; a half-written
file is removed. Other panes showing a changed directory refresh themselves (`FileBrowserChanges`).
Seen working on screen against a real SFTP server in both directions. Found on the way, and fixed:
clicking empty space in a file-browser pane never focused it (Fluent's `ListBox` is not focusable, so
`_list.Focus()` did nothing — including when the shell moved focus there by key); a cancelled
operation never said so; closing a remote pane could block the UI thread on a busy connection.

**2026-09-16** — CI, docs site, in-app updater, SSH/RDP/SFTP/FTP profiles, file operations, the
0.7.0 release. `git log` and `CHANGELOG.md` have the detail.

## The next action

**Use it for an hour, and write down what annoyed you.** Every feature on the list is in and has been
seen working; the remaining bugs are the kind only use finds. Today's session proved it again — the
focus bug above had survived every test and was found in two minutes of clicking.

If engineering time is wanted before that, the candidates are taste calls, not gaps — decide they are
wanted first: **drag and drop** between file-browser panes (paste works; dragging does not exist);
**multi-select** in the browser (the clipboard holds one entry because the list selects one);
and the two from the 2026-09-15 critique — a toolbar without a divider after every group, and a
Normal/Compact density setting (`LayoutMetrics` is already parameterised).

## Waiting on you

Only a person, a machine setting or a judgement can move these; nothing in the code waits on them.

- **Run the mixed-DPI check.** Set one display to a different scale, then
  `scripts/verify-mixed-dpi.ps1`. The last measurement in the project never taken — this machine has
  never had two monitors at different scales.
- **Decide `Terminal.Emulation`.** [ADR 0018](docs/adr/0018-terminal-emulation-supply-chain.md) has
  four costed options and a recommendation (ask the author to publish the source; keep hashes pinned;
  benchmark `Iciclecreek.Avalonia.Terminal`). Nobody has asked the author; that is a message, not a
  commit.

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

**Testing the UI**
- Do not point a headless test at the shared temp folder when *where the pointer lands* matters. The
  rest of the suite fills it while running, and the test passed alone and failed in the suite.
- Do not script clicks at absolute screen coordinates across a relaunch — WinMux restores its window
  wherever the session last saved it. Read the window rect and click relative to it.
- Do not add an overload pair `RunAsync(Action)` / `RunAsync(Func<Task>)` to a test helper. An
  `async () => { … }` lambda binds to the `Action` one as `async void`, and every exception inside
  it — including a failed assertion — is discarded.
- Do not assume `HeadlessUnitTestSession.Dispatch`'s void overload awaits the body. It does not: a
  synchronous throw surfaces, and anything after the first `await` is dropped. `Headless.RunAsync`
  returns a value so it binds to the overload that awaits, and `HeadlessHarnessTests` pins it.
  Between those two faults the headless suite passed unconditionally for a while, with
  `Assert.Fail` as the first statement of a test.
- Do not trust a new guard until it has failed. The pane-construction tests were only believable
  once the `KeyGesture.Parse("Del")` bug was put back and two of them went red with the exact error
  the user had reported.

**Build and tooling**
- Do not commit the `packages.lock.json` changes that a publish produces. `dotnet publish -r win-x64`
  adds an empty `net10.0/win-x64` section to every lock file; CI restores *without* a RID and
  `--locked-mode` then fails NU1004 across the whole solution. It looked like part of the version
  bump and went in with it, turning CI red. `publish.ps1` now snapshots the lock files and puts them
  back, because `RestorePackagesWithLockFile=false` is refused outright (NU1005) while they exist.
- Do not check that a packaged file *exists* and call it verified. `publish.ps1` required both
  `WinMux.exe` and `winmux.exe`, and `UpdateInstaller` checks archives for `WinMux.exe` — all three
  were satisfied by one file, because Windows filenames are case-insensitive and the CLI had
  overwritten the shell. Check what the file is: the PE subsystem is 2 for the GUI and 3 for a
  console application.
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
- Do not call `Focus()` on a Fluent `ListBox` and assume it worked. It is not focusable — its rows
  are — so the call returns false and nothing happens. The file browser's `FocusList()` does it right.
- Do not dispose a remote filesystem on the UI thread. Dispose takes the connection lock, which a
  listing from a dead server or another pane's transfer may hold for minutes.
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
