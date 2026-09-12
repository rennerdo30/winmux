[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sessionDirectory = Join-Path $repo 'artifacts\phase2-demo'
$sessionPath = Join-Path $sessionDirectory 'session.toml'
$executable = Join-Path $repo "WinMux.Shell\bin\x64\$Configuration\net10.0-windows\WinMux.exe"
$cli = Join-Path $repo "WinMux.Cli\bin\$Configuration\net10.0\winmux.exe"

function ConvertTo-TomlString([string]$Value) {
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'WinMux.slnx') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "WinMux build failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath $executable)) {
    throw "WinMux.exe was not found at $executable. Run without -NoBuild first."
}
if (-not (Test-Path -LiteralPath $cli)) {
    throw "winmux.exe was not found at $cli. Run without -NoBuild first."
}

New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $sessionPath)) {
    $cwd = ConvertTo-TomlString $repo
    $cmd = ConvertTo-TomlString $env:ComSpec
    $timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $session = @"
# Visible Phase 2 acceptance session. This file is intentionally safe to edit.
version = 1
saved_at = $timestamp

[[windows]]
title = 'Phase 2 — window one'
root = 'w1-root'
focused = '11111111-1111-1111-1111-111111111111'
bounds = { x = 80, y = 80, width = 900, height = 560 }

[[windows.nodes]]
id = 'w1-root'
kind = 'leaf'
pane = '11111111-1111-1111-1111-111111111111'

[[windows.panes]]
id = '11111111-1111-1111-1111-111111111111'
kind = 'terminal'
title = 'cwd + env demo one'
program = $cmd
args = ['/d', '/k', 'echo restored env: %WINMUX_PHASE2_DEMO% & cd']
env = { WINMUX_PHASE2_DEMO = 'window-one' }
cwd = $cwd
cwd_source = 'launch-directory'
cwd_captured_at = $timestamp

[[windows]]
title = 'Phase 2 — window two'
root = 'w2-root'
focused = '22222222-2222-2222-2222-222222222222'
bounds = { x = 1040, y = 120, width = 760, height = 500 }

[[windows.nodes]]
id = 'w2-root'
kind = 'leaf'
pane = '22222222-2222-2222-2222-222222222222'

[[windows.panes]]
id = '22222222-2222-2222-2222-222222222222'
kind = 'terminal'
title = 'cwd + env demo two'
program = $cmd
args = ['/d', '/k', 'echo restored env: %WINMUX_PHASE2_DEMO% & cd']
env = { WINMUX_PHASE2_DEMO = 'window-two' }
cwd = $cwd
cwd_source = 'launch-directory'
cwd_captured_at = $timestamp
"@
    [IO.File]::WriteAllText($sessionPath, $session, [Text.UTF8Encoding]::new($false))
}

& $cli validate $sessionPath
if ($LASTEXITCODE -ne 0) { throw "The generated demo session did not validate." }
if ($PrepareOnly) {
    Write-Host "Prepared and validated $sessionPath" -ForegroundColor Green
    return
}

Write-Host ''
Write-Host 'PHASE 2 VISIBLE CHECK — FIRST RUN' -ForegroundColor Cyan
Write-Host '  1. Two WinMux windows should open at different positions.'
Write-Host '  2. Each terminal should print its distinct restored environment value and cwd.'
Write-Host '  3. In either pane, run: cd /d %TEMP%'
Write-Host '  4. Resize/move the windows and wait one second. Do NOT close them.'
Write-Host '  5. The cwd setup dialog explains and can install shell reporting.'
Write-Host ''
Write-Host "Session under test: $sessionPath"

$first = Start-Process -FilePath $executable -ArgumentList @($sessionPath) -PassThru
Read-Host 'When the changed cwd and geometry are visible, press Enter here to simulate a crash'
Start-Sleep -Milliseconds 750
Stop-Process -Id $first.Id -Force
$first.WaitForExit()
Write-Host 'WinMux was force-stopped. The session below must still contain the latest debounced state.' -ForegroundColor Yellow

Write-Host ''
Write-Host 'Saved cwd evidence:' -ForegroundColor Green
Select-String -LiteralPath $sessionPath -Pattern '^cwd\s*=|^cwd_source\s*=|^cwd_captured_at\s*=' |
    ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }

Read-Host 'Press Enter to relaunch the saved session and verify geometry/cwd restore'
$second = Start-Process -FilePath $executable -ArgumentList @($sessionPath) -PassThru
$second.WaitForExit()
if ($second.ExitCode -ne 0) { throw "Restored WinMux run exited with code $($second.ExitCode)." }

Write-Host ''
Write-Host 'Visible Phase 2 check complete.' -ForegroundColor Green
Write-Host "The editable session remains at $sessionPath"
