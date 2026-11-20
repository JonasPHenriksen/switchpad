@echo off
rem Double-click after flashing the SD card with Raspberry Pi Imager.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-sd.ps1" %*
pause
