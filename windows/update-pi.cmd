@echo off
rem Copies the newest Pi files to the running Pi over SSH, re-runs its install and restarts it.
rem Usage: update-pi.cmd USER@switchpad.local   (or USER@<the Pi's IP address>)
if "%~1"=="" (echo Usage: update-pi.cmd USER@switchpad.local & pause & exit /b 1)
echo Copying the Pi files to %1 (enter the Pi's password if asked)...
scp "%~dp0..\pi\switchpad.py" "%~dp0..\pi\install.sh" %1:/tmp/ || (echo Copy FAILED & pause & exit /b 1)
ssh -t %1 "sudo cp /tmp/switchpad.py /tmp/install.sh /boot/firmware/switchpad/ && sudo bash /boot/firmware/switchpad/install.sh --no-reboot > /dev/null && sudo systemctl restart switchpad && sleep 3 && systemctl is-active switchpad && journalctl -u switchpad -n 15 --no-pager"
echo.
echo If config.txt changed (e.g. Bluetooth radio switched off), that applies after the next power off/on.
pause
