<#
.SYNOPSIS
    Build WinMux and run it.

.DESCRIPTION
    The short path for trying the app. Builds the solution, then launches the shell with an
    optional session file. Use scripts\publish.ps1 when you want a standalone dist\ folder
    instead of running out of the build tree.

.EXAMPLE
    .\scripts\run.ps1
    Build and run with the default session (a cmd pane beside the file browser).

.EXAMPLE
    .\scripts\run.ps1 -Session .\examples\cmd-and-explorer.toml
    Build and run a saved layout.

.EXAMPLE
    .\scripts\run.ps1 -NoBuild -Wait
    Run what is already built and keep the console attached until the window closes.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # A session .toml to open. Defaults to WinMux's own default layout.
    [string]$Session,

    # Run the already-built binaries instead of building first.
    [switch]$NoBuild,

    # Block until the window closes, and report its exit code.
    [switch]$Wait,

    # Anything after -- is passed through to WinMux.exe unchanged.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Passthrough
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$shell = Join-Path $repo "WinMux.Shell\bin\x64\$Configuration\net10.0-windows\WinMux.exe"

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'WinMux.slnx') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "WinMux build failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath $shell)) {
    throw "WinMux.exe is not at $shell. Run without -NoBuild, or check the configuration."
}

# A missing PaneHost only shows up much later, as a foreign-app pane that refuses to start.
$paneHost = Join-Path (Split-Path -Parent $shell) 'WinMux.PaneHost.exe'
if (-not (Test-Path -LiteralPath $paneHost)) {
    throw "WinMux.PaneHost.exe is missing beside WinMux.exe. Foreign-app panes cannot start; rebuild without -NoBuild."
}

$arguments = @()
if ($Session) {
    $resolved = (Resolve-Path -LiteralPath $Session).Path
    $arguments += $resolved
    Write-Host "Session: $resolved"
}
if ($Passthrough) { $arguments += $Passthrough }

Write-Host "Running:  $shell"
if ($Wait) {
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -PassThru -Wait
    Write-Host "WinMux exited with code $($process.ExitCode)."
    exit $process.ExitCode
}

Start-Process -FilePath $shell -ArgumentList $arguments | Out-Null
