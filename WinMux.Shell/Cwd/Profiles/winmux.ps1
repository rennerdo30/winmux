# WinMux cwd reporting for PowerShell 5.1 and PowerShell 7+.
# The installer imports this file from the user's existing profile without replacing it.

if (-not (Test-Path variable:global:__winmuxInnerPrompt)) {
    $global:__winmuxInnerPrompt = $function:prompt
}

function global:prompt {
    $loc = $ExecutionContext.SessionState.Path.CurrentLocation
    if ($loc.Provider.Name -eq 'FileSystem') {
        [Console]::Write("$([char]27)]9;9;$($loc.ProviderPath)$([char]7)")
    }
    & $global:__winmuxInnerPrompt
}
