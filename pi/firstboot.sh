#!/bin/bash
# One-time SwitchPad setup. prepare-sd.ps1 starts it from cmdline.txt
# (systemd.run=...), so it runs on the next boot without needing cloud-init,
# a network or a keyboard. The Pi reboots by itself when it finishes
# (systemd.run_success_action / systemd.run_failure_action in cmdline.txt).
B=/boot/firmware
[ -f "$B/cmdline.txt" ] || B=/boot
D="$B/switchpad"

say() { echo "$*"; echo "firstboot: $*" > /dev/tty1 2>/dev/null; }

# 1. Remove our boot options first, so this can never run twice or loop.
sed -i -e 's| systemd\.run=[^ ]*||g' \
       -e 's| systemd\.run_success_action=[^ ]*||g' \
       -e 's| systemd\.run_failure_action=[^ ]*||g' \
       -e 's| systemd\.unit=kernel-command-line\.target||g' "$B/cmdline.txt"
sync
say "SwitchPad one-time setup started (removed the boot option again)."

# 2. Diagnostics onto the card, readable from Windows.
{
    echo "=== firstboot $(date) ==="
    echo "--- cloud-init status"
    timeout 20 cloud-init status --long 2>&1 || true
    echo "--- /etc/hostname: $(cat /etc/hostname 2>/dev/null)"
    echo "--- users with a home: $(ls /home 2>/dev/null)"
    echo "--- Wi-Fi: $(iw dev wlan0 link 2>&1 | head -3 | tr '\n' ' ')"
} > "$D/diag.txt" 2>&1
for f in /var/log/cloud-init.log /var/log/cloud-init-output.log; do
    [ -f "$f" ] && tail -n 400 "$f" > "$D/$(basename "$f")"
done
sync

# 3. The actual install (writes install.log next to it), then reboot.
bash "$D/install.sh" --no-reboot
say "Setup finished, rebooting."
sync
exit 0
