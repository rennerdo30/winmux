<#
.SYNOPSIS
    Install the setup program silently into a scratch folder, check what it did, and uninstall it.

.DESCRIPTION
    A setup program that compiles is not one that installs. This runs the real thing, the way a
    user's machine would, and checks the three promises ADR 0030 makes:

      - it installs per-user, with no administrator rights, into a folder WinMux can update;
      - Windows lists it, with the version, where the in-app updater can find it; and
      - uninstalling removes the folder entirely, including files a later update added.

    Safe on a developer machine: it installs into a temporary folder, checks first that no
    WinMux is already installed under the same AppId, and uninstalls afterwards whatever happens.

.EXAMPLE
    .\scripts\test-installer.ps1 -Setup dist\WinMux-0.7.8-win-x64-setup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Setup
)

$ErrorActionPreference = 'Stop'
$appId = '{8D6AF144-4CE1-44AA-83DD-DBA38FEF9DA8}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"

if (Test-Path -LiteralPath $uninstallKey) {
    throw "WinMux is already installed for this user ($((Get-ItemProperty $uninstallKey).InstallLocation)). " +
          'This test would replace and then uninstall it; run it on a machine without an installed WinMux.'
}

$Setup = (Resolve-Path -LiteralPath $Setup).Path
$target = Join-Path ([System.IO.Path]::GetTempPath()) "winmux-install-test-$([guid]::NewGuid().ToString('N'))"
$log = "$target.log"

function Check([bool]$condition, [string]$what) {
    if (-not $condition) { throw "Installer check failed: $what" }
    Write-Host "  ok  $what"
}

try {
    Write-Host "Installing $Setup into $target ..." -ForegroundColor Cyan
    $install = Start-Process -FilePath $Setup -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$target", "/LOG=$log")
    Check ($install.ExitCode -eq 0) "setup exits 0 (got $($install.ExitCode); log: $log)"

    $exe = Join-Path $target 'WinMux.exe'
    Check (Test-Path -LiteralPath $exe) 'WinMux.exe is installed'
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $subsystem = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 0x5C)
    Check ($subsystem -eq 2) 'WinMux.exe is the windowed application, not the CLI'
    foreach ($file in 'wmux.exe', 'WinMux.PaneHost.exe', 'foreign-app-quirks.json', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') {
        Check (Test-Path -LiteralPath (Join-Path $target $file)) "$file is installed"
    }

    Check (Test-Path -LiteralPath $uninstallKey) 'Windows lists it under the per-user AppId the updater writes to'
    $entry = Get-ItemProperty -LiteralPath $uninstallKey
    Check ($entry.InstallLocation.TrimEnd('\') -ieq $target) "the listed location is the install folder ($($entry.InstallLocation))"
    Check ([bool]$entry.DisplayVersion) "a version is listed ($($entry.DisplayVersion))"

    # Write access is the whole reason it is per-user: the in-app updater replaces these files.
    $probe = Join-Path $target 'write-probe.tmp'
    Set-Content -LiteralPath $probe -Value 'probe'
    Remove-Item -LiteralPath $probe
    Check $true 'the install folder is writable by the user, so in-app updates can replace files'

    # Something only an update would have put there, and a leftover the updater renames aside.
    Set-Content -LiteralPath (Join-Path $target 'added-by-an-update.dll') -Value 'x'
    Set-Content -LiteralPath (Join-Path $target 'WinMux.dll.winmux-old') -Value 'x'

    Write-Host 'Uninstalling ...' -ForegroundColor Cyan
    $uninstaller = Join-Path $target 'unins000.exe'
    Check (Test-Path -LiteralPath $uninstaller) 'an uninstaller is installed'
    $uninstall = Start-Process -FilePath $uninstaller -Wait -PassThru -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    Check ($uninstall.ExitCode -eq 0) "uninstall exits 0 (got $($uninstall.ExitCode))"

    # The uninstaller hands its own deletion to a copy of itself that runs on after it exits.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Test-Path -LiteralPath $target) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Check (-not (Test-Path -LiteralPath $target)) 'the install folder is gone, files added by updates included'
    Check (-not (Test-Path -LiteralPath $uninstallKey)) 'Windows no longer lists it'

    Write-Host 'The setup program installs and uninstalls cleanly.' -ForegroundColor Green
}
finally {
    $uninstaller = Join-Path $target 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        Start-Process -FilePath $uninstaller -Wait -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
}
