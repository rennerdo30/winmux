<#
.SYNOPSIS
    Package WinMux into a versioned release folder and zip under dist\.

.DESCRIPTION
    Produces dist\WinMux-<version>-win-x64\ and the matching .zip: WinMux.exe, the wmux CLI,
    WinMux.PaneHost.exe, the shipped foreign-app quirks database, the example sessions, LICENSE and
    a short README. The folder is deliberately flat — PaneHost has to sit beside WinMux.exe,
    because that is where the shell looks for it.

    Framework-dependent by default, so it needs the .NET 10 desktop runtime. -SelfContained
    produces a copy that carries its own runtime and needs nothing installed.

.EXAMPLE
    .\scripts\publish.ps1
    Package the current version into dist\.

.EXAMPLE
    .\scripts\publish.ps1 -SelfContained
    Package a copy that runs on a machine with no .NET.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Where to put the release folder. Defaults to dist\WinMux-<version>-win-x64.
    [string]$Destination,

    # Carry the .NET runtime along, for a machine that has none.
    [switch]$SelfContained,

    # Skip the .zip and leave only the folder.
    [switch]$NoArchive
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$shellProject = Join-Path $repo 'WinMux.Shell\WinMux.Shell.csproj'

# One source of truth: Directory.Build.props. Reading it back from MSBuild rather than repeating
# it here means the folder name can never disagree with what the binaries report about themselves.
$version = (dotnet msbuild $shellProject -getProperty:Version -p:Platform=x64 | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'Could not read the product version from MSBuild.' }

$name = "WinMux-$version-win-x64"
if ($SelfContained) { $name += '-selfcontained' }
if (-not $Destination) { $Destination = Join-Path $repo "dist\$name" }

if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path

$common = @(
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '-o', $Destination
)

Write-Host "Packaging WinMux $version ($Configuration, win-x64)..." -ForegroundColor Cyan

# The shell carries PaneHost and the quirks database with it (see CopyPaneHostOnPublish).
dotnet publish $shellProject @common
if ($LASTEXITCODE -ne 0) { throw "Publishing WinMux.Shell failed with exit code $LASTEXITCODE." }

dotnet publish (Join-Path $repo 'WinMux.Cli\WinMux.Cli.csproj') @common
if ($LASTEXITCODE -ne 0) { throw "Publishing WinMux.Cli failed with exit code $LASTEXITCODE." }

# Things a person unzipping this will want and cannot rebuild.
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $Destination

# The licences of everything we ship beside our own code. MIT, BSD and Apache all require the
# notice to travel with the binaries, and about forty third-party DLLs are in this folder.
& (Join-Path $PSScriptRoot 'third-party-notices.ps1') -Destination $Destination
$examples = Join-Path $Destination 'examples'
New-Item -ItemType Directory -Path $examples -Force | Out-Null
Copy-Item -Path (Join-Path $repo 'examples\*.toml') -Destination $examples

@"
WinMux $version (win-x64)
=========================

A window multiplexer for Windows: tmux's layout model applied to whole applications.

Run it
------
  WinMux.exe                       the default layout
  WinMux.exe examples\tabs-and-splits.toml    a layout with nested tab groups

Note that a session file is LIVE: WinMux saves your layout back to the file you opened, so
copy an example before editing it if you want to keep the original.

$(if ($SelfContained) { "This copy carries its own .NET runtime; nothing needs to be installed." } else { "Requires the .NET 10 desktop runtime: https://dotnet.microsoft.com/download" })

Everything is on the toolbar. The keyboard is tmux-style: Ctrl+B then % or " to split,
arrows to move focus, c for a tab, v for a tab group with tabs down the side, x to close,
: for the command palette.

foreign-app-quirks.json beside this file is yours to edit; it maps applications to an
embedding strategy and a window-selection rule.

Source and issues: https://github.com/rennerdo30/winmux
MIT licensed; see LICENSE. The components shipped alongside it are listed in
THIRD-PARTY-NOTICES.txt.

The command line is wmux.exe in this folder: wmux help lists what it can do.
"@ | Set-Content -LiteralPath (Join-Path $Destination 'README.txt') -Encoding utf8

# Verify rather than trust. Each of these is invisible until it is needed at runtime.
$required = @(
    @{ Path = 'WinMux.exe';              Why = 'the shell itself' },
    @{ Path = 'WinMux.PaneHost.exe';     Why = 'foreign-app panes cannot start without it' },
    @{ Path = 'wmux.exe';                Why = 'the command line surface' },
    @{ Path = 'foreign-app-quirks.json'; Why = 'foreign-app selection rules' },
    @{ Path = 'LICENSE';                 Why = 'the licence this ships under' },
    @{ Path = 'THIRD-PARTY-NOTICES.txt'; Why = 'the licences of everything shipped beside us' },
    @{ Path = 'README.txt';              Why = 'how to run it' }
)
$missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Destination $_.Path)) }
if ($missing) {
    $detail = ($missing | ForEach-Object { "$($_.Path) ($($_.Why))" }) -join '; '
    throw "Packaging produced an incomplete release: missing $detail"
}

# Existence is not enough, and this is not hypothetical. The CLI used to be called winmux.exe,
# which on a case-insensitive filesystem IS WinMux.exe: it published second, overwrote the shell,
# and every package shipped the console CLI under the name the README tells people to run. The
# check above passed the whole time, because both entries resolved to the one surviving file.
#
# So check what the file *is*. A Windows PE header records its subsystem: 2 = GUI, 3 = console.
function Get-PESubsystem {
    param([string] $Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $peHeader = [BitConverter]::ToInt32($bytes, 0x3C)
    return [BitConverter]::ToUInt16($bytes, $peHeader + 0x5C)
}

$shellExe = Join-Path $Destination 'WinMux.exe'
$cliExe = Join-Path $Destination 'wmux.exe'

$shellSubsystem = Get-PESubsystem $shellExe
if ($shellSubsystem -ne 2) {
    throw "WinMux.exe is not a GUI application (subsystem $shellSubsystem, expected 2). Something overwrote the shell."
}

$cliSubsystem = Get-PESubsystem $cliExe
if ($cliSubsystem -ne 3) {
    throw "wmux.exe is not a console application (subsystem $cliSubsystem, expected 3)."
}

# And that they really are two files, not one name seen twice.
if ((Get-Item $shellExe).Length -eq (Get-Item $cliExe).Length -and
    (Get-FileHash $shellExe).Hash -eq (Get-FileHash $cliExe).Hash) {
    throw "WinMux.exe and wmux.exe are the same file. The shell and the CLI have collided again."
}

$size = (Get-ChildItem -LiteralPath $Destination -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "Release folder: $Destination  ($([math]::Round($size / 1MB, 1)) MB)" -ForegroundColor Green

if (-not $NoArchive) {
    $archive = Join-Path (Split-Path -Parent $Destination) "$name.zip"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $Destination '*') -DestinationPath $archive -CompressionLevel Optimal
    $zipSize = (Get-Item -LiteralPath $archive).Length
    Write-Host "Archive:        $archive  ($([math]::Round($zipSize / 1MB, 1)) MB)" -ForegroundColor Green
}

Write-Host ''
Write-Host "Run it with:    $(Join-Path $Destination 'WinMux.exe')"
