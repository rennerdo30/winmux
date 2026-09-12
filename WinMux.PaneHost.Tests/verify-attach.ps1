#requires -Version 7.0

param(
    [string] $PaneHost = (Join-Path $PSScriptRoot '..\WinMux.PaneHost\bin\Release\net10.0-windows\WinMux.PaneHost.exe'),
    [ValidateSet('attach', 'embed')]
    [string] $Strategy = 'attach'
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
    $start = [Diagnostics.ProcessStartInfo]::new($PaneHost)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.Arguments = "--program charmap.exe --strategy $Strategy"

    $process = [Diagnostics.Process]::Start($start)
    Assert-True ($null -ne $process) 'PaneHost did not start.'

    $hostLine = $process.StandardOutput.ReadLine()
    $strategyLine = $process.StandardOutput.ReadLine()
    $readyLine = $process.StandardOutput.ReadLine()
    Assert-True ($hostLine -match '^HOST_HWND=([0-9]+)$') "Unexpected host line: $hostLine"
    $hostHwnd = [IntPtr][long]$Matches[1]
    Assert-True ($strategyLine -eq "STRATEGY=$Strategy") "Unexpected strategy line: $strategyLine"
    Assert-True ($readyLine -match '^READY=([0-9]+)$') "Unexpected ready line: $readyLine"
    $childHwnd = [IntPtr][long]$Matches[1]

    $desktop = [WinMuxAttachVerifierNative]::GetDesktopWindow()
    $expectedParent = if ($Strategy -eq 'attach') { $desktop } else { $hostHwnd }
    Assert-True ([WinMuxAttachVerifierNative]::GetAncestor($childHwnd, 1) -eq $expectedParent) `
        "Application parent does not match $Strategy strategy."

    Assert-True ([WinMuxAttachVerifierNative]::SetWindowPos(
        $hostHwnd, [IntPtr]::Zero, 180, 210, 640, 480, 0x0010 -bor 0x0040)) `
        'Could not move the PaneHost test window.'
    Start-Sleep -Milliseconds 750

    $hostRect = [WinMuxAttachVerifierNative+Rect]::new()
    $childRect = [WinMuxAttachVerifierNative+Rect]::new()
    Assert-True ([WinMuxAttachVerifierNative]::GetWindowRect($hostHwnd, [ref]$hostRect)) 'Could not read host rectangle.'
    Assert-True ([WinMuxAttachVerifierNative]::GetWindowRect($childHwnd, [ref]$childRect)) 'Could not read child rectangle.'
    Assert-True ([Math]::Abs($hostRect.Left - $childRect.Left) -le 2) 'Attached application did not follow host X.'
    Assert-True ([Math]::Abs($hostRect.Top - $childRect.Top) -le 2) 'Attached application did not follow host Y.'
    Assert-True ([Math]::Abs(($hostRect.Right - $hostRect.Left) - ($childRect.Right - $childRect.Left)) -le 2) `
        'Attached application did not follow host width.'
    Assert-True ([Math]::Abs(($hostRect.Bottom - $hostRect.Top) - ($childRect.Bottom - $childRect.Top)) -le 2) `
        'Attached application did not follow host height.'

    $process.StandardInput.WriteLine('DETACH')
    $process.StandardInput.Flush()
    $exited = $process.WaitForExit(10000)
    Assert-True $exited "PaneHost did not exit after DETACH (pid=$($process.Id), responding=$($process.Responding), hwnd=$($process.MainWindowHandle))."
    Assert-True ($process.ExitCode -eq 0) "PaneHost exited with code $($process.ExitCode)."
    Assert-True ([WinMuxAttachVerifierNative]::IsWindow($childHwnd)) 'Application did not survive DETACH.'
    Assert-True ([WinMuxAttachVerifierNative]::GetAncestor($childHwnd, 1) -eq $desktop) `
        'Detached application is not top-level.'

    Write-Output "PaneHost $Strategy verification: PASS"
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
