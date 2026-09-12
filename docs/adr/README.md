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
