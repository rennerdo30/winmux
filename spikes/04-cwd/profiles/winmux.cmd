@echo off
rem WinMux cwd reporting for cmd.exe.
rem
rem Use as an AutoRun entry:
rem   reg add "HKCU\Software\Microsoft\Command Processor" /v AutoRun /d "path\to\winmux.cmd" /f
rem
rem Emits OSC 9;9 with the current directory as part of the prompt string. $E is the escape
rem character and $P the current path; the sequence is terminated with ST (ESC backslash).
rem
rem A nested cmd.exe inherits PROMPT through the environment, so nested shells keep reporting.

prompt $e]9;9;$P$e\$P$G
