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
$demoDirectory = Join-Path $repo 'artifacts\phase4-demo'
$leftDirectory = Join-Path $demoDirectory 'left pane'
$selectedDirectory = Join-Path $leftDirectory 'selected folder'
$rightDirectory = Join-Path $demoDirectory '資料 right pane'
$selectedFile = Join-Path $rightDirectory 'mémo 日本語.txt'
$missingDirectory = Join-Path $demoDirectory 'deleted before restore'
$sessionPath = Join-Path $demoDirectory 'session.toml'
$shell = Join-Path $repo "WinMux.Shell\bin\x64\$Configuration\net10.0-windows\WinMux.exe"
$cli = Join-Path $repo "WinMux.Cli\bin\$Configuration\net10.0\winmux.exe"

function ConvertTo-TomlString([string]$Value) {
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'WinMux.slnx') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "WinMux build failed with exit code $LASTEXITCODE." }
}

foreach ($required in @($shell, $cli)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required Phase 4 artifact is missing: $required" }
}

New-Item -ItemType Directory -Path $selectedDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $rightDirectory -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $leftDirectory 'alpha.txt'), 'left file', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($selectedFile, 'Unicode and spaces survive.', [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $missingDirectory) {
    Remove-Item -LiteralPath $missingDirectory -Recurse -Force
}

$left = ConvertTo-TomlString $leftDirectory
$selected = ConvertTo-TomlString $selectedDirectory
$right = ConvertTo-TomlString $rightDirectory
$rightSelection = ConvertTo-TomlString $selectedFile
$missing = ConvertTo-TomlString $missingDirectory
$repoCwd = ConvertTo-TomlString $repo
$cmd = ConvertTo-TomlString $env:ComSpec
$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$session = @"
# Visible Phase 4 acceptance session: two independent browsers, terminal, and restore failure.
version = 1
saved_at = $timestamp

[[windows]]
title = 'Phase 4 — pane providers and file browser'
root = 'root'
focused = '11111111-1111-1111-1111-111111111111'
bounds = { x = 60, y = 60, width = 1540, height = 860 }

[[windows.nodes]]
id = 'root'
kind = 'split'
direction = 'columns'
children = ['left', 'middle', 'right']
ratios = [0.34, 0.32, 0.34]

[[windows.nodes]]
id = 'left'
kind = 'leaf'
pane = '11111111-1111-1111-1111-111111111111'

[[windows.nodes]]
id = 'middle'
kind = 'split'
direction = 'rows'
children = ['terminal', 'missing']
ratios = [0.52, 0.48]

[[windows.nodes]]
id = 'terminal'
kind = 'leaf'
pane = '22222222-2222-2222-2222-222222222222'

[[windows.nodes]]
id = 'missing'
kind = 'leaf'
pane = '33333333-3333-3333-3333-333333333333'

[[windows.nodes]]
id = 'right'
kind = 'leaf'
pane = '44444444-4444-4444-4444-444444444444'

[[windows.panes]]
id = '11111111-1111-1111-1111-111111111111'
kind = 'file-browser'
title = 'left — selected directory'
extras = { current_directory = $left, selected_path = $selected }

[[windows.panes]]
id = '22222222-2222-2222-2222-222222222222'
kind = 'terminal'
title = 'Phase 4 controls'
program = $cmd
args = ['/d', '/k', 'echo Ctrl+B then T opens a terminal from the focused file browser.']
cwd = $repoCwd
cwd_source = 'launch-directory'
cwd_captured_at = $timestamp

[[windows.panes]]
id = '33333333-3333-3333-3333-333333333333'
kind = 'file-browser'
title = 'missing restore intent'
extras = { current_directory = $missing, selected_path = '' }

[[windows.panes]]
id = '44444444-4444-4444-4444-444444444444'
kind = 'file-browser'
title = 'right — Unicode and spaces'
extras = { current_directory = $right, selected_path = $rightSelection }
"@
[IO.File]::WriteAllText($sessionPath, $session, [Text.UTF8Encoding]::new($false))

& $cli validate $sessionPath
if ($LASTEXITCODE -ne 0) { throw 'The generated Phase 4 demo session did not validate.' }

if ($VerifyOnly) {
    dotnet test (Join-Path $repo 'WinMux.Shell.Tests\WinMux.Shell.Tests.csproj') `
        -c $Configuration --no-restore `
        --filter 'FullyQualifiedName~FileBrowser|FullyQualifiedName~PaneProvider|FullyQualifiedName~AsyncActionDispatcher'
    if ($LASTEXITCODE -ne 0) { throw 'Phase 4 focused tests failed.' }
    Write-Host "Phase 4 checks passed; visible fixture prepared at $sessionPath" -ForegroundColor Green
    return
}

if ($PrepareOnly) {
    Write-Host "Prepared and validated $sessionPath" -ForegroundColor Green
    return
}

Write-Host ''
Write-Host 'PHASE 4 VISIBLE CHECK' -ForegroundColor Cyan
Write-Host '  • Left and right file panes must navigate independently; Enter opens a directory, Backspace goes up, F5 refreshes.'
Write-Host '  • The right pane restores a Unicode path and selected file.'
Write-Host '  • The lower-middle pane explains that its saved directory is missing and keeps that restore intent.'
Write-Host '  • Focus the left pane and use Ctrl+B then T: the new terminal tab must start in "selected folder".'
Write-Host '  • Ctrl+B then 5 opens another file-browser tab; the command palette and CLI expose the same named actions.'
Write-Host '  • Close and rerun with -NoBuild to see current directories and selections restore.'
Write-Host ''
Write-Host "Editable session: $sessionPath"

$process = Start-Process -FilePath $shell -ArgumentList @($sessionPath) -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "WinMux exited with code $($process.ExitCode)." }

& $cli validate $sessionPath
if ($LASTEXITCODE -ne 0) { throw 'The saved Phase 4 session did not validate after the walkthrough.' }
$saved = [IO.File]::ReadAllText($sessionPath)
if (-not $saved.Contains('current_directory') -or -not $saved.Contains('selected_path')) {
    throw 'The saved session lost file-browser restore fields.'
}
Write-Host 'Visible Phase 4 check complete; file-browser restore fields remain valid.' -ForegroundColor Green
