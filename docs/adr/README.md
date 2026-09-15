# Architecture decision records

One short file per decision, numbered in order. Written *when the decision is made*,
not afterwards — see CLAUDE.md section 8.

Copy `0000-template.md`. Every ADR carries a "What failed" section.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-out-of-process-pane-hosts.md) | Out-of-process pane hosts, and the rule that actually keeps the shell alive | accepted |
| [0002](0002-terminal-stack.md) | Terminal stack: .NET 10 + Avalonia, adopted VT engine behind our own interface | accepted |
| [0003](0003-foreign-app-compatibility.md) | Foreign-app embedding: what "any Windows app" actually means | accepted |
| [0004](0004-cwd-capture.md) | Capturing the working directory: ship the shell snippets | accepted |
| [0005](0005-layout-engine.md) | Layout engine: canonical mutable tree, geometric focus, format-neutral snapshots | accepted |
| [0006](0006-session-file-format.md) | Session file format: TOML, with the layout tree flattened | accepted |
| [0007](0007-hosting-foreign-windows.md) | Hosting foreign windows: NativeControlHost, and embed only | accepted |
| [0008](0008-pane-host-ipc.md) | Out-of-process pane-host IPC and detach cleanup | accepted |
| [0009](0009-phase-1-terminal-runtime.md) | Phase 1 terminal runtime and named actions | accepted |
| [0010](0010-phase-2-persistence-runtime.md) | Phase 2 persistence runtime and cwd recovery | accepted |
| [0011](0011-phase-3-foreign-app-runtime.md) | Phase 3 foreign-app runtime and compatibility selection | accepted |
| [0012](0012-phase-4-pane-providers-and-file-browser.md) | Phase 4 pane providers and built-in file browser | accepted |
| [0013](0013-phase-5-platform-layer.md) | Phase 5 platform layer | accepted |
| [0014](0014-nested-tab-groups-and-shell-chrome.md) | Nested tab groups, tab placement, and a real toolbar | accepted |
| [0015](0015-profiles-app-catalog-and-empty-panes.md) | Profiles, the application catalogue, and empty panes | accepted |
| [0016](0016-windows-11-chrome.md) | Looking like Windows 11 | accepted |
| [0017](0017-input-queue-attachment.md) | Input-queue attachment: measured | accepted |
| [0018](0018-terminal-emulation-supply-chain.md) | `Terminal.Emulation`: the supply-chain position | accepted for 0.x |
| [0019](0019-network-filesystems.md) | Network filesystems: delegate to Windows, ship no client | accepted |
| [0020](0020-sftp-and-ftp.md) | SFTP and FTP: implement them, because nothing else will | accepted |
