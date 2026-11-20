#!/bin/bash
# SwitchPad installer for Raspberry Pi OS Lite on a Pi Zero W.
# Usage: sudo bash install.sh [--no-reboot]
# prepare-sd.ps1 makes this run automatically on the next boot via firstboot.sh
# (with --no-reboot; the Pi reboots by itself afterwards). A log is written next to this script, so you
# can read it from Windows by putting the SD card back into the PC.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo: sudo bash $0"; exit 1; }
HERE="$(cd "$(dirname "$0")" && pwd)"
REBOOT=1
[ "${1:-}" = "--no-reboot" ] && REBOOT=0

# Log to the card (readable from Windows) and to the HDMI screen.
exec > >(tee -a "$HERE/install.log" /dev/tty1) 2>&1
echo "=== SwitchPad install $(date) ==="

CONFIG=/boot/firmware/config.txt
[ -f "$CONFIG" ] || CONFIG=/boot/config.txt

echo "1/5 Enabling USB device mode in $CONFIG"
# Look for our exact line: Raspberry Pi OS already has "dtoverlay=dwc2,dr_mode=host" under
# [cm5], which doesn't apply to the Pi Zero. prepare-sd.ps1 normally adds this line already.
if ! grep -q '^dtoverlay=dwc2,dr_mode=peripheral' "$CONFIG"; then
    printf '\n[all]\ndtoverlay=dwc2,dr_mode=peripheral\n' >> "$CONFIG"
fi
for m in dwc2 libcomposite; do
    grep -qx "$m" /etc/modules || echo "$m" >> /etc/modules
done

echo "2/5 Switching the Pi's Bluetooth radio off (it shares one chip with Wi-Fi)"
if ! grep -q '^dtoverlay=disable-bt' "$CONFIG"; then
    printf '\n[all]\ndtoverlay=disable-bt\n' >> "$CONFIG"
fi
systemctl disable hciuart.service bluetooth.service 2>/dev/null || true

echo "3/5 Turning off Wi-Fi power saving (it causes 100+ ms input lag spikes)"
if [ -d /etc/NetworkManager/conf.d ]; then
    printf '[connection]\nwifi.powersave = 2\n' > /etc/NetworkManager/conf.d/switchpad-wifi-powersave.conf
fi

echo "4/5 Installing /opt/switchpad"
install -d /opt/switchpad
install -m 755 "$HERE/switchpad.py" /opt/switchpad/switchpad.py

echo "5/5 Installing systemd service"
cat > /etc/systemd/system/switchpad.service <<'EOF'
[Unit]
Description=SwitchPad (Wi-Fi -> Switch USB controller bridge)
After=sys-kernel-config.mount network.target

[Service]
ExecStartPre=-/usr/sbin/iw dev wlan0 set power_save off
# Prefer the copy on the SD card's boot partition, so updates are just "copy the file in Windows".
ExecStart=/bin/sh -c 'for f in /boot/firmware/switchpad/switchpad.py /boot/switchpad/switchpad.py /opt/switchpad/switchpad.py; do if [ -f "$$f" ]; then exec /usr/bin/python3 "$$f" run; fi; done; exit 1'
Restart=always
RestartSec=2

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable switchpad.service

echo "USB device mode line in config.txt: $(grep -c '^dtoverlay=dwc2,dr_mode=peripheral' "$CONFIG")"
echo "INSTALL OK (v3: Wi-Fi only, runs switchpad.py from the SD card). After the next boot, start SwitchPadBridge.exe on the PC."
sync
if [ "$REBOOT" = 1 ]; then
    echo "Rebooting in 5 seconds."
    sleep 5
    reboot
fi
