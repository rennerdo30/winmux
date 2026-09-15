# Samples

## WinMux.SampleProvider — a pane kind WinMux does not ship

[ADR 0012](../docs/adr/0012-phase-4-pane-providers-and-file-browser.md) made `PaneKind` a string
rather than an enum so that a provider in another assembly could define its own kind. That claim
went unverified for two phases, because nothing loaded an external provider: the extension point was
real in the type system and nowhere else.

This is the smallest thing that exercises it end to end — a pane that shows the time.

### Installing it

```powershell
dotnet build samples\WinMux.SampleProvider -c Release

$app  = "WinMux.Shell\bin\x64\Release\net10.0-windows"
$dest = "$app\providers\WinMux.SampleProvider"
New-Item -ItemType Directory -Force $dest
Copy-Item samples\WinMux.SampleProvider\bin\Release\net10.0\WinMux.SampleProvider.dll $dest

& "$app\WinMux.exe" examples\external-provider.toml
```

The right-hand pane is a `com.example.clock`, provided entirely from outside WinMux.

### The rules

- Providers live in `providers/` beside `WinMux.exe`, **one directory per provider**, each holding
  its assembly and whatever it depends on privately. One directory apiece rather than one flat
  folder because two providers wanting different versions of the same library is the normal case.
- The directory is **named after the assembly**: `providers/Acme.Provider/Acme.Provider.dll`.
- A provider type is public, non-abstract, implements `IPaneProvider`, and has a **public
  parameterless constructor**.
- It must not claim a `PaneKind` something else already provides. Use a reverse-DNS kind
  (`com.example.clock`) and a collision becomes impossible by accident.
- Do **not** ship `WinMux.Panes`, `WinMux.Core` or Avalonia in the provider's folder. They have to
  be the shell's copies or your `IPaneProvider` is a different type from the one the registry wants
  — the classic plugin failure, and a confusing one because the type names match. The sample's
  project reference uses `Private="false"` for exactly this.

### When it does not load

Every refusal is reported in the status bar with the reason, because a plugin that does not appear
and does not say why is the worst outcome available. The rules that produce those messages are
`PaneProviderDiscovery`, and they are unit-tested in `WinMux.Shell.Tests/PaneProviderDiscoveryTests.cs`.

A session referring to a kind no installed provider handles is **not** destroyed: the descriptor
round-trips through the session file untouched and the pane opens as unavailable, saying which kind
is missing (ADR 0012).
