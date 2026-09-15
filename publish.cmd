@echo off
rem Package WinMux into a versioned release folder and zip under dist\.
rem     publish.cmd                 framework-dependent (needs the .NET 10 desktop runtime)
rem     publish.cmd -SelfContained  carries its own runtime, installs nothing
rem
rem See the note in run.cmd for why this is a wrapper rather than batch.
setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish.ps1" %*
set "CODE=%ERRORLEVEL%"
rem Packaging prints where it put things, so a double-click always waits before closing.
echo %cmdcmdline% | find /i "%~nx0" >nul && pause
endlocal & exit /b %CODE%
