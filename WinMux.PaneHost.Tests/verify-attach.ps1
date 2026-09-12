#requires -Version 7.0

param(
    [string] $PaneHost = (Join-Path $PSScriptRoot '..\WinMux.PaneHost\bin\x64\Release\net10.0-windows\WinMux.PaneHost.exe'),
    [string] $Program = 'charmap.exe',
    [string] $WindowClass,
    [ValidateSet('attach', 'embed')]
    [string] $Strategy = 'attach',
    [ValidateSet('', 'attach', 'embed')]
    [string] $ExpectedStrategy = '',
    [switch] $RequireFallbackNotice,
    [switch] $ExerciseSwitch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinMuxAttachVerifierNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$process = $null
$childHwnd = [IntPtr]::Zero
try {
    if ([string]::IsNullOrEmpty($ExpectedStrategy)) { $ExpectedStrategy = $Strategy }
    $start = [Diagnostics.ProcessStartInfo]::new($PaneHost)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    [void]$start.ArgumentList.Add('--program')
    [void]$start.ArgumentList.Add($Program)
    [void]$start.ArgumentList.Add('--strategy')
    [void]$start.ArgumentList.Add($Strategy)
    if (-not [string]::IsNullOrWhiteSpace($WindowClass)) {
        [void]$start.ArgumentList.Add('--window-class')
        [void]$start.ArgumentList.Add($WindowClass)
        [void]$start.ArgumentList.Add('--match-mode')
        [void]$start.ArgumentList.Add('class-name')
    }

    $process = [Diagnostics.Process]::Start($start)
    Assert-True ($null -ne $process) 'PaneHost did not start.'

    $hostLine = $process.StandardOutput.ReadLine()
    Assert-True ($hostLine -match '^HOST_HWND=([0-9]+)$') "Unexpected host line: $hostLine"
    $hostHwnd = [IntPtr][long]$Matches[1]

    $noticeLine = $null
    $effectiveStrategy = $null
    while ($null -eq $childHwnd -or $childHwnd -eq [IntPtr]::Zero) {
        $line = $process.StandardOutput.ReadLine()
        Assert-True ($null -ne $line) 'PaneHost exited before READY.'
        if ($line -match '^NOTICE=(.+)$') { $noticeLine = $Matches[1]; continue }
        if ($line -match '^STRATEGY=(attach|embed)$') { $effectiveStrategy = $Matches[1]; continue }
        if ($line -match '^READY=([0-9]+)$') { $childHwnd = [IntPtr][long]$Matches[1]; break }
        if ($line -match '^ERROR=(.+)$') { throw "PaneHost error: $($Matches[1])" }
        throw "Unexpected PaneHost line: $line"
    }
    Assert-True ($effectiveStrategy -eq $ExpectedStrategy) `
        "Expected effective strategy $ExpectedStrategy, got $effectiveStrategy."
    if ($RequireFallbackNotice) {
        Assert-True (-not [string]::IsNullOrWhiteSpace($noticeLine)) 'Expected a visible fallback notice.'
        Assert-True ($noticeLine -match 'fallback=attach') "Fallback notice was not machine-detectable: $noticeLine"
    }

    $desktop = [WinMuxAttachVerifierNative]::GetDesktopWindow()
    $expectedParent = if ($effectiveStrategy -eq 'attach') { $desktop } else { $hostHwnd }
    Assert-True ([WinMuxAttachVerifierNative]::GetAncestor($childHwnd, 1) -eq $expectedParent) `
        "Application parent does not match $effectiveStrategy strategy."

    Assert-True ([WinMuxAttachVerifierNative]::SetWindowPos(
        $hostHwnd, [IntPtr]::Zero, 180, 160, 720, 680, 0x0010 -bor 0x0040)) `
        'Could not move the PaneHost test window.'
    Start-Sleep -Milliseconds 750

    $hostRect = [WinMuxAttachVerifierNative+Rect]::new()
    $childRect = [WinMuxAttachVerifierNative+Rect]::new()
    Assert-True ([WinMuxAttachVerifierNative]::GetWindowRect($hostHwnd, [ref]$hostRect)) 'Could not read host rectangle.'
    Assert-True ([WinMuxAttachVerifierNative]::GetWindowRect($childHwnd, [ref]$childRect)) 'Could not read child rectangle.'
    Assert-True ([Math]::Abs($hostRect.Left - $childRect.Left) -le 2) 'Attached application did not follow host X.'
    Assert-True ([Math]::Abs($hostRect.Top - $childRect.Top) -le 2) 'Attached application did not follow host Y.'
    $hostWidth = $hostRect.Right - $hostRect.Left
    $hostHeight = $hostRect.Bottom - $hostRect.Top
    $childWidth = $childRect.Right - $childRect.Left
    $childHeight = $childRect.Bottom - $childRect.Top
    Assert-True ([Math]::Abs($hostWidth - $childWidth) -le 2) `
        "Attached application did not follow host width (host=$hostWidth, app=$childWidth)."
    Assert-True ([Math]::Abs($hostHeight - $childHeight) -le 2) `
        "Attached application did not follow host height (host=$hostHeight, app=$childHeight)."

    [void][WinMuxAttachVerifierNative]::ShowWindow($hostHwnd, 0)
    Start-Sleep -Milliseconds 250
    Assert-True (-not [WinMuxAttachVerifierNative]::IsWindowVisible($childHwnd)) `
        'Application stayed visible after its PaneHost was hidden.'
    [void][WinMuxAttachVerifierNative]::ShowWindow($hostHwnd, 8)
    Start-Sleep -Milliseconds 250
    Assert-True ([WinMuxAttachVerifierNative]::IsWindowVisible($childHwnd)) `
        'Application did not return when its PaneHost was shown.'

    if ($ExerciseSwitch) {
        $originalEffective = $effectiveStrategy
        $other = if ($effectiveStrategy -eq 'embed') { 'attach' } else { 'embed' }
        foreach ($target in @($other, $originalEffective)) {
            $process.StandardInput.WriteLine("STRATEGY=$($target.ToUpperInvariant())")
            $process.StandardInput.Flush()
            $switchedStrategy = $null
            $switchedChild = [IntPtr]::Zero
            while ($switchedChild -eq [IntPtr]::Zero) {
                $line = $process.StandardOutput.ReadLine()
                Assert-True ($null -ne $line) 'PaneHost exited during a live strategy switch.'
                if ($line -match '^NOTICE=(.+)$') { continue }
                if ($line -match '^STRATEGY=(attach|embed)$') { $switchedStrategy = $Matches[1]; continue }
                if ($line -match '^READY=([0-9]+)$') { $switchedChild = [IntPtr][long]$Matches[1]; break }
                if ($line -match '^ERROR=(.+)$') { throw "PaneHost switch error: $($Matches[1])" }
                throw "Unexpected PaneHost switch line: $line"
            }
            Assert-True ($switchedChild -eq $childHwnd) 'Live strategy switch adopted a different application window.'
            Assert-True ($switchedStrategy -eq $target) "Expected switch to $target, got $switchedStrategy."
            $switchParent = if ($target -eq 'attach') { $desktop } else { $hostHwnd }
            Assert-True ([WinMuxAttachVerifierNative]::GetAncestor($childHwnd, 1) -eq $switchParent) `
                "Application parent does not match live $target strategy."
        }
    }

    $process.StandardInput.WriteLine('DETACH')
    $process.StandardInput.Flush()
    $detachLine = $process.StandardOutput.ReadLine()
    Assert-True ($detachLine -eq 'DETACHED') "PaneHost did not acknowledge a verified detach: $detachLine"
    $exited = $process.WaitForExit(10000)
    Assert-True $exited "PaneHost did not exit after DETACH (pid=$($process.Id), responding=$($process.Responding), hwnd=$($process.MainWindowHandle))."
    Assert-True ($process.ExitCode -eq 0) "PaneHost exited with code $($process.ExitCode)."
    Assert-True ([WinMuxAttachVerifierNative]::IsWindow($childHwnd)) 'Application did not survive DETACH.'
    Assert-True ([WinMuxAttachVerifierNative]::GetAncestor($childHwnd, 1) -eq $desktop) `
        'Detached application is not top-level.'

    $fallback = if ($null -ne $noticeLine) { "; notice=$noticeLine" } else { '' }
    Write-Output "PaneHost requested=$Strategy effective=$effectiveStrategy verification: PASS$fallback"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.StandardInput.WriteLine('DETACH'); $process.StandardInput.Flush() } catch { }
        if (-not $process.WaitForExit(3000)) { $process.Kill() }
    }
    if ($childHwnd -ne [IntPtr]::Zero -and [WinMuxAttachVerifierNative]::IsWindow($childHwnd)) {
        [void][WinMuxAttachVerifierNative]::PostMessageW($childHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    if ($null -ne $process) { $process.Dispose() }
}
