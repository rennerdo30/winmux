<#
.SYNOPSIS
    Verify pane placement across monitors running at different display scales.

.DESCRIPTION
    The last thing in CLAUDE.md that has needed a human since Phase 0.

    ADR 0003 measured foreign-window hosting at a single scale and found a consistent one-pixel
    error from DPI virtualization. What has never been exercised is a WinMux window *spanning two
    monitors at different scales*, which is where the bugs are expected to be: the shell is
    per-monitor-v2, a hosted application may be anything from unaware to per-monitor-v2, and the
    pane rectangle crosses the boundary between them.

    Nobody could run this because the development machine has two monitors at the same scale. This
    script removes every part of that except the sixty seconds of changing one of them.

    It refuses to report anything if the monitors are not actually at different scales. That
    control case is not decoration: two harnesses in this repository have produced confident,
    wrong answers because nothing checked their preconditions (ADR 0016, ADR 0017).

.EXAMPLE
    # 1. Settings > System > Display: set one monitor to 150% and the other to 100%.
    # 2. Sign out and back in if Windows asks.
    .\scripts\verify-mixed-dpi.ps1

.EXAMPLE
    .\scripts\verify-mixed-dpi.ps1 -NoBuild -Seconds 30
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Skip the build and use what is already there.
    [switch]$NoBuild,

    # How long to leave the window up for a human to look at it.
    [int]$Seconds = 25,

    # Report the monitor layout and stop. Use this to check the precondition before committing to a run.
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Dpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, Proc cb, IntPtr data);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

  public delegate bool Proc(IntPtr mon, IntPtr hdc, IntPtr rect, IntPtr data);
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public class Screen { public int Left, Top, Width, Height, DpiPercent; }

  public static List<Screen> Monitors() {
    var found = new List<Screen>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (mon, hdc, rect, data) => {
      uint x, y; GetDpiForMonitor(mon, 0, out x, out y);
      var mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(mi);
      GetMonitorInfo(mon, ref mi);
      found.Add(new Screen {
        Left = mi.rcMonitor.Left, Top = mi.rcMonitor.Top,
        Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
        Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
        DpiPercent = (int)(x / 96.0 * 100) });
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

# Without this every coordinate below is virtualised and the whole run measures nothing.
# That mistake cost most of a session once; see ADR 0016.
[void][Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4))

$monitors = [Dpi]::Monitors()
Write-Output "monitors:"
foreach ($m in $monitors) {
    Write-Output ("  {0}x{1} at {2},{3} — {4}%" -f $m.Width, $m.Height, $m.Left, $m.Top, $m.DpiPercent)
}

$scales = $monitors | ForEach-Object { $_.DpiPercent } | Sort-Object -Unique
if ($monitors.Count -lt 2) {
    throw "This needs two monitors. Found $($monitors.Count)."
}
if ($scales.Count -lt 2) {
    throw @"
All monitors are at $($scales[0])%, so there is no mixed scale to verify and any result would be
meaningless. Set one of them to a different scale in Settings > System > Display > Scale, then run
this again. That is the whole reason this has needed a human since Phase 0.
"@
}

Write-Output ""
Write-Output "mixed scales present: $($scales -join '%, ')% — precondition met."
if ($CheckOnly) { return }

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'WinMux.slnx') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "WinMux build failed with exit code $LASTEXITCODE." }
}

$shell = Join-Path $repo "WinMux.Shell\bin\x64\$Configuration\net10.0-windows\WinMux.exe"
if (-not (Test-Path -LiteralPath $shell)) { throw "WinMux.exe is not at $shell." }

# A session with a terminal and a file browser side by side, so the window can be stretched across
# the boundary and both halves observed. A foreign-app pane is the interesting case and is left to
# the human: open one with the New menu once the window is up, and drag it across the seam.
$session = Join-Path $env:TEMP "winmux-mixed-dpi.toml"
$left = $monitors[0]
$right = $monitors[1]
$spanX = [Math]::Min($left.Left, $right.Left) + 200
$spanWidth = [Math]::Abs($right.Left - $left.Left) + 800

@"
version = 1
saved_at = 2026-09-15T12:00:00.000Z

[[windows]]
title   = 'mixed dpi'
root    = 'n0'
focused = '11111111-1111-1111-1111-111111111111'
bounds  = { x = $spanX, y = 200, width = $spanWidth, height = 900 }

[[windows.nodes]]
id = 'n0'
kind = 'split'
direction = 'columns'
children = ['n1', 'n2']
ratios = [0.5, 0.5]

[[windows.nodes]]
id = 'n1'
kind = 'leaf'
pane = '11111111-1111-1111-1111-111111111111'

[[windows.nodes]]
id = 'n2'
kind = 'leaf'
pane = '22222222-2222-2222-2222-222222222222'

[[windows.panes]]
id = '11111111-1111-1111-1111-111111111111'
kind = 'terminal'
title = 'left'
program = 'C:\WINDOWS\system32\cmd.exe'

[[windows.panes]]
id = '22222222-2222-2222-2222-222222222222'
kind = 'file-browser'
title = 'right'
"@ | Set-Content -LiteralPath $session -Encoding UTF8

Write-Output ""
Write-Output "launching WinMux spanning both monitors..."
$proc = Start-Process -FilePath $shell -ArgumentList "`"$session`"" -PassThru
try {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and $proc.MainWindowHandle -eq 0) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
    }
    if ($proc.MainWindowHandle -eq 0) { throw "WinMux did not open a window." }

    Start-Sleep -Seconds 6
    [void][Dpi]::SetForegroundWindow($proc.MainWindowHandle)

    $r = New-Object Dpi+RECT
    [void][Dpi]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
    Write-Output ("window: {0},{1} to {2},{3} ({4}x{5})" -f $r.Left, $r.Top, $r.Right, $r.Bottom, ($r.Right-$r.Left), ($r.Bottom-$r.Top))

    $seam = $right.Left
    if ($r.Left -lt $seam -and $r.Right -gt $seam) {
        Write-Output "the window spans the seam at x=$seam — this is the case that has never been tested."
    } else {
        Write-Output "WARNING: the window does not span both monitors; drag it across the seam by hand."
    }

    $shot = Join-Path $env:TEMP "winmux-mixed-dpi.png"
    $virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $virtual.Width, $virtual.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($virtual.Location, [System.Drawing.Point]::Empty, $virtual.Size)
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()

    Write-Output ""
    Write-Output "screenshot: $shot"
    Write-Output ""
    Write-Output "WHAT TO LOOK FOR, while the window is up ($Seconds seconds):"
    Write-Output "  1. Text size — is the terminal the same physical size on both halves?"
    Write-Output "  2. The divider — does dragging it track the pointer across the seam?"
    Write-Output "  3. Open a foreign app (New > an application) and drag the window across the seam."
    Write-Output "     ADR 0003 measured a one-pixel error at a single scale; look for worse here."
    Write-Output "  4. The caption buttons and the snap flyout on the half that is not primary."

    Start-Sleep -Seconds $Seconds
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    Remove-Item -LiteralPath $session -ErrorAction SilentlyContinue
}
