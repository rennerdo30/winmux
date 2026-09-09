<#
  Runs the full spike-3 matrix and prints a summary table.

  The hang matrix is run in three layout modes because the spike found two
  independent freeze mechanisms:
    sync     - shell repositions the pane from its UI thread (what a naive shell does)
    async    - same, but with SWP_ASYNCWINDOWPOS
    nolayout - shell never touches the pane window, isolating input-queue attachment
#>
param(
    [string]$Configuration = 'Release',
    [string]$LogRoot = "$env:TEMP\winmux-spike3",
    [int]$HangMs = 6000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet build "$root\HangSpike.csproj" -c $Configuration -v q --nologo | Out-Null
$exe = Get-ChildItem "$root\bin\$Configuration" -Recurse -Filter HangSpike.exe | Select-Object -First 1
if (-not $exe) { throw "HangSpike.exe not found under $root\bin\$Configuration" }

if (Test-Path $LogRoot) { Remove-Item $LogRoot -Recurse -Force }
New-Item -ItemType Directory -Force $LogRoot | Out-Null

$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Run([string]$scenario, [string]$mode) {
    $tag = "$scenario-$mode"
    $dir = Join-Path $LogRoot $tag
    $extra = @()
    if ($mode -eq 'async')    { $extra += '--asyncpos' }
    if ($mode -eq 'nolayout') { $extra += '--nolayout' }

    Write-Host "==> $tag" -ForegroundColor Cyan
    $out = & $exe.FullName --role shell --scenario $scenario --logdir $dir --hangms $HangMs @extra 2>&1
    $verdict = ($out | Select-String -Pattern '^\S+ \[shell/\d+\] VERDICT' | Select-Object -Last 1)
    if ($verdict) { Write-Host "    $($verdict.Line -replace '^\S+ \[shell/\d+\] ','')" }

    $line = ($out | Select-String -Pattern 'HANG      uiTicks' | Select-Object -Last 1)
    $results.Add([pscustomobject]@{
        Scenario = $scenario
        Mode     = $mode
        Verdict  = if ($verdict) { ($verdict.Line -split 'SHELL |: ')[-1] } else { 'NO RESULT' }
        Detail   = if ($line) { ($line.Line -replace '^\S+ \[shell/\d+\] ','') } else { '' }
    })

    Get-Process HangSpike -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

foreach ($s in 'T1', 'T2', 'T3', 'T4') {
    foreach ($m in 'sync', 'async', 'nolayout') { Invoke-Run $s $m }
}
foreach ($s in 'T5', 'T6') { Invoke-Run $s 'sync' }

Write-Host ''
Write-Host '================ SPIKE 3 SUMMARY ================' -ForegroundColor Yellow
$results | Format-Table Scenario, Mode, Verdict -AutoSize
Write-Host "Logs: $LogRoot"
