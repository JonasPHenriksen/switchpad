@echo off
rem Packs all SwitchPad logs into one zip. Optional: collect-logs.cmd USER@switchpad.local
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-logs.ps1" %*
pause
