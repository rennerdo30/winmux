[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$PrepareOnly,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sessionDirectory = Join-Path $repo 'artifacts\phase3-demo'
$sessionPath = Join-Path $sessionDirectory 'session.toml'
$shell = Join-Path $repo "WinMux.Shell\bin\x64\$Configuration\net10.0-windows\WinMux.exe"
$cli = Join-Path $repo "WinMux.Cli\bin\$Configuration\net10.0\winmux.exe"
$paneHost = Join-Path $repo "WinMux.PaneHost\bin\x64\$Configuration\net10.0-windows\WinMux.PaneHost.exe"
$verifier = Join-Path $repo 'WinMux.PaneHost.Tests\verify-attach.ps1'

function ConvertTo-TomlString([string]$Value) {
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'WinMux.slnx') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "WinMux build failed with exit code $LASTEXITCODE." }
}

foreach ($required in @($shell, $cli, $paneHost, $verifier)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required Phase 3 artifact is missing: $required" }
}

Write-Host 'Running measured foreign-window checks…' -ForegroundColor Cyan
& $verifier -PaneHost $paneHost -Strategy embed -ExerciseSwitch
& $verifier -PaneHost $paneHost -Program (Join-Path $env:WINDIR 'System32\calc.exe') `
    -WindowClass ApplicationFrameWindow -Strategy embed -ExpectedStrategy attach -RequireFallbackNotice

if ($VerifyOnly) {
    Write-Host 'Phase 3 measured checks passed.' -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null
$cwd = ConvertTo-TomlString $repo
$cmd = ConvertTo-TomlString $env:ComSpec
$charmap = ConvertTo-TomlString (Join-Path $env:WINDIR 'System32\charmap.exe')
$calculator = ConvertTo-TomlString (Join-Path $env:WINDIR 'System32\calc.exe')
$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$session = @"
# Visible Phase 3 acceptance session. The two foreign panes use strategy='auto'.
version = 1
saved_at = $timestamp

[[windows]]
title = 'Phase 3 — foreign apps'
root = 'root'
focused = '11111111-1111-1111-1111-111111111111'
bounds = { x = 80, y = 80, width = 1500, height = 760 }

[[windows.nodes]]
id = 'root'
kind = 'split'
direction = 'columns'
children = ['terminal', 'charmap', 'calculator']
ratios = [0.25, 0.375, 0.375]

[[windows.nodes]]
id = 'terminal'
kind = 'leaf'
pane = '11111111-1111-1111-1111-111111111111'

[[windows.nodes]]
id = 'charmap'
kind = 'leaf'
pane = '22222222-2222-2222-2222-222222222222'

[[windows.nodes]]
id = 'calculator'
kind = 'leaf'
pane = '33333333-3333-3333-3333-333333333333'

[[windows.panes]]
id = '11111111-1111-1111-1111-111111111111'
kind = 'terminal'
title = 'Phase 3 controls'
program = $cmd
args = ['/d', '/k', 'echo Ctrl+B then A toggles the focused foreign pane between embed and attach.']
cwd = $cwd
cwd_source = 'launch-directory'
cwd_captured_at = $timestamp

[[windows.panes]]
id = '22222222-2222-2222-2222-222222222222'
kind = 'foreign-app'
title = 'Character Map — measured embed'
program = $charmap
strategy = 'auto'
extras = { window_class = '#32770' }

[[windows.panes]]
id = '33333333-3333-3333-3333-333333333333'
kind = 'foreign-app'
title = 'Calculator — measured attach'
program = $calculator
strategy = 'auto'
extras = { window_class = 'ApplicationFrameWindow' }
"@
[IO.File]::WriteAllText($sessionPath, $session, [Text.UTF8Encoding]::new($false))

& $cli validate $sessionPath
if ($LASTEXITCODE -ne 0) { throw 'The generated Phase 3 demo session did not validate.' }
if ($PrepareOnly) {
    Write-Host "Prepared and validated $sessionPath" -ForegroundColor Green
    return
}

Write-Host ''
Write-Host 'PHASE 3 VISIBLE CHECK' -ForegroundColor Cyan
Write-Host '  • Character Map should be borderless and truly embedded in the middle pane.'
Write-Host '  • Calculator should remain top-level but track the right pane (automatic packaged-app rule).'
Write-Host '  • Use Ctrl+B then Left/Right to focus a foreign pane; Ctrl+B then A switches its strategy live.'
Write-Host '  • Resize/move WinMux: both apps must follow without freezing the terminal.'
Write-Host '  • Closing WinMux detaches both apps so they survive; closing a pane with Ctrl+B then X closes its app.'
Write-Host ''
Write-Host "Editable session: $sessionPath"

$process = Start-Process -FilePath $shell -ArgumentList @($sessionPath) -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "WinMux exited with code $($process.ExitCode)." }

Write-Host 'Visible Phase 3 check complete. Any surviving foreign apps were intentionally detached.' -ForegroundColor Green
