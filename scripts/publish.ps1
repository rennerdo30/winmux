<#
.SYNOPSIS
    Publish WinMux into dist\ as a self-contained folder you can copy anywhere.

.DESCRIPTION
    Produces dist\<Configuration>\ containing WinMux.exe, the winmux CLI, WinMux.PaneHost.exe and
    the shipped foreign-app quirks database. The folder is deliberately flat: PaneHost has to sit
    beside WinMux.exe, because that is where the shell looks for it.

    Framework-dependent by default — it needs the .NET 10 desktop runtime. Pass -SelfContained for
    a copy that carries its own runtime and needs nothing installed.

.EXAMPLE
    .\scripts\publish.ps1
    Publish Release into dist\Release.

.EXAMPLE
    .\scripts\publish.ps1 -SelfContained
    Publish a copy that runs on a machine with no .NET installed.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Where to publish. Defaults to dist\<Configuration> at the repository root.
    [string]$Destination,

    # Carry the .NET runtime along, for a machine that has none.
    [switch]$SelfContained,

    # Remove the destination before publishing, so a stale file can never survive a rename.
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repo "dist\$Configuration" }

if ($Clean -and (Test-Path -LiteralPath $Destination)) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path

$common = @(
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '-o', $Destination
)

# The shell carries PaneHost and the quirks database with it (see CopyPaneHostOnPublish).
dotnet publish (Join-Path $repo 'WinMux.Shell\WinMux.Shell.csproj') @common
if ($LASTEXITCODE -ne 0) { throw "Publishing WinMux.Shell failed with exit code $LASTEXITCODE." }

dotnet publish (Join-Path $repo 'WinMux.Cli\WinMux.Cli.csproj') @common
if ($LASTEXITCODE -ne 0) { throw "Publishing WinMux.Cli failed with exit code $LASTEXITCODE." }

# Verify rather than trust. Each of these is invisible until it is needed at runtime.
$required = @(
    @{ Path = 'WinMux.exe';          Why = 'the shell itself' },
    @{ Path = 'WinMux.PaneHost.exe'; Why = 'foreign-app panes cannot start without it' },
    @{ Path = 'winmux.exe';          Why = 'the command line surface' },
    @{ Path = 'foreign-app-quirks.json'; Why = 'foreign-app selection rules' }
)
$missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Destination $_.Path)) }
if ($missing) {
    $detail = ($missing | ForEach-Object { "$($_.Path) ($($_.Why))" }) -join '; '
    throw "Publish produced an incomplete dist: missing $detail"
}

$size = (Get-ChildItem -LiteralPath $Destination -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "Published to $Destination ($([math]::Round($size / 1MB, 1)) MB)"
Write-Host "Run it with:  $(Join-Path $Destination 'WinMux.exe')"
