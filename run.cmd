@echo off
rem Build WinMux and run it. Double-click this, or pass arguments through:
rem     run.cmd -Session examples\tabs-and-splits.toml
rem
rem A thin wrapper on purpose. The work lives in scripts\run.ps1, because the checks it makes
rem (did the build actually succeed, is PaneHost beside the exe) are the kind of thing batch gets
rem wrong quietly. What this adds is the two things a .ps1 cannot do for itself: run on a
rem double-click, and run without the user first changing their execution policy.
setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run.ps1" %*
set "CODE=%ERRORLEVEL%"
rem Pause only when Explorer launched this, so the window does not vanish with the error in it.
echo %cmdcmdline% | find /i "%~nx0" >nul && if not "%CODE%"=="0" pause
endlocal & exit /b %CODE%
