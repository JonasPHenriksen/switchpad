@echo off
rem Prints what's on the Pi's SD card. Copy the whole output and send it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-sd.ps1"
pause
