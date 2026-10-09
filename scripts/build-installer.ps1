<#
.SYNOPSIS
    Build the WinMux setup program from a folder publish.ps1 produced.

.DESCRIPTION
    Produces dist\WinMux-<version>-win-x64-setup.exe from dist\WinMux-<version>-win-x64\ using
    installer\WinMux.iss. The zip stays the release's update package; the setup program is how a
    person installs WinMux the first time (ADR 0030).

    Needs Inno Setup 7's ISCC.exe: pass -Iscc, put it on PATH, or let scripts\install-inno-setup.ps1
    fetch the pinned version.

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\build-installer.ps1 -Iscc (.\scripts\install-inno-setup.ps1 -Destination $env:TEMP\inno)
#>
[CmdletBinding()]
param(
    # The release folder. Defaults to dist\WinMux-<version>-win-x64.
    [string]$SourceDirectory,

    # Path to ISCC.exe. Defaults to the one on PATH.
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$shellProject = Join-Path $repo 'WinMux.Shell\WinMux.Shell.csproj'

# The same single source of truth publish.ps1 reads: the git tag, through MinVer. `-t:MinVer` is
# required — without it MSBuild answers with the property as evaluated, before any target has run,
# which is the 1.0.0 it invents when nobody has said otherwise.
$version = (dotnet msbuild $shellProject -t:MinVer -getProperty:Version -p:Platform=x64 -nologo |
    Where-Object { $_.Trim() } |
    Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'Could not read the product version from MSBuild.' }

# Windows file versions are four numbers; "0.7.8-test.8" becomes 0.7.8.0.
$numeric = ($version -split '[-+]')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "The product version '$version' is not major.minor.patch[-suffix]." }
$numeric += '.0'

if (-not $SourceDirectory) { $SourceDirectory = Join-Path $repo "dist\WinMux-$version-win-x64" }
if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'WinMux.exe'))) {
    throw "No WinMux.exe in $SourceDirectory. Run scripts\publish.ps1 first."
}
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path

if (-not $Iscc) {
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { $Iscc = $found.Source }
    if (-not $Iscc) { throw 'ISCC.exe was not found. Pass -Iscc, or run scripts\install-inno-setup.ps1.' }
}

$output = Join-Path $repo 'dist'
$script = Join-Path $repo 'installer\WinMux.iss'

Write-Host "Building the WinMux $version setup program..." -ForegroundColor Cyan
& $Iscc "/DAppVersion=$version" "/DNumericVersion=$numeric" "/DSourceDir=$SourceDirectory" "/DOutputDir=$output" '/Q' $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$setup = Join-Path $output "WinMux-$version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC reported success but $setup does not exist." }

Write-Host "Setup program:  $setup  ($([math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
return $setup
