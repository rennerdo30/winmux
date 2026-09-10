# WinMux cwd reporting for PowerShell 5.1 and PowerShell 7+.
#
# Add to your profile:   . "path\to\winmux.ps1"      ($PROFILE shows where that lives)
#
# Emits OSC 9;9 with the current filesystem path before each prompt. Wraps whatever prompt you
# already have rather than replacing it.

if (-not (Test-Path variable:global:__winmuxInnerPrompt)) {
    $global:__winmuxInnerPrompt = $function:prompt
}

function global:prompt {
    $loc = $ExecutionContext.SessionState.Path.CurrentLocation
    # Only filesystem locations are meaningful; Registry:: and friends are not directories.
    if ($loc.Provider.Name -eq 'FileSystem') {
        [Console]::Write("$([char]27)]9;9;$($loc.ProviderPath)$([char]7)")
    }
    & $global:__winmuxInnerPrompt
}
