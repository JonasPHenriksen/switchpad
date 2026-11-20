# SwitchPad – Switch 1 controller bridge (Pi Zero W)

```
Parsec guest pad -> Parsec virtual Xbox pad (PC) -> SwitchPadBridge.exe
   -> Wi-Fi -> Pi Zero W -> its "USB" port -> Switch dock
Switch HDMI -> capture card -> PC -> Parsec
```

The Pi shows up on the Switch as a plain wired pad (HORI Pokken style). That means
no motion controls, no rumble and no amiibo. Every normal button and stick works.

> **Why not Bluetooth?** We tried it first. On this hardware the PC ↔ Pi Bluetooth link kept
> disconnecting every few seconds, and it simply isn't worth the effort to make it work
> properly. Wi-Fi works, so SwitchPad is Wi-Fi only, and the Pi's Bluetooth radio is
> switched off to give Wi-Fi the radio chip to itself.

## What you need

- The Pi Zero W (V1.1) and its microSD card. **The card is wiped in step 1.**
  Back it up first if the old setup on it matters to you.
- An SD card reader.
- A micro-USB **data** cable. Many cheap cables only charge and will not work.
- A **2.4 GHz Wi-Fi** that both the Pi and the PC running Parsec are on. A normal router is
  best. A laptop's mobile hotspot also works but has more hiccups (see "Wi-Fi lag").
  The PC itself can be on Ethernet, as long as it's on the same router.
- This whole project folder (`pi` and `windows`) on the PC you prepare the card with.
- [Raspberry Pi Imager](https://www.raspberrypi.com/software/).

## 1. Flash the SD card

In Raspberry Pi Imager:

1. **Device:** Raspberry Pi Zero
2. **OS:** Raspberry Pi OS (other) → **Raspberry Pi OS Lite (32-bit)**
3. **Storage:** the SD card
4. In **Edit settings / Customisation** (required):
   - Hostname: `switchpad`
   - Username and password: your choice
   - **Wi-Fi:** your network's name and password (the Pi Zero W only does 2.4 GHz)
   - **Enable SSH** (recommended: it's the easiest way to update the Pi and collect logs)

## 2. Prepare the card

When Imager finishes, **take the card out and put it back in** so Windows shows the
`bootfs` drive. Then double-click **`windows\prepare-sd.cmd`**. It:
- finds the card,
- copies the Pi files onto it,
- turns on USB device mode and switches the unused Bluetooth radio off in `config.txt`,
- adds a one-time boot option to `cmdline.txt`, so the install runs on the next boot.
  The install removes that option again as its very first step.

Wait for `Done.` and eject the card.

## 3. First boot (automatic install)

Put the card in the Pi, connect HDMI if you can, and power it from the **PWR IN** port
(any USB charger). Then leave it alone for about **5 minutes**. If you're watching the
screen you'll see lines starting `firstboot:`, then `1/5 ... 5/5` and `INSTALL OK`, and
the Pi reboots. A freshly flashed card may also reboot once for its own setup.

The install also saves diagnostics onto the card (`switchpad\diag.txt` and the
`cloud-init` logs). `windows\check-sd.cmd` prints them.

## 4. Find the Pi on the network

Run `windows\SwitchPadBridge.exe --test`. By default it looks for `switchpad.local`.
The line `Wi-Fi: switchpad.local is 192.168.x.y` shows the Pi's IP address. From then on start it with
that address, e.g. `SwitchPadBridge.exe --pi 192.168.1.50`: looking up the name can take
10+ seconds on Windows. Your router's device list also shows the IP.

If the Pi is never found, check that it's on the same Wi-Fi as the PC (router device list,
or the HDMI screen) and that the Wi-Fi details in step 1 were right.

## 5. Test on the PC first (no Switch needed)

1. Unplug the Pi. Connect the Pi's **USB** port (the middle one, not PWR IN) to the PC.
   That one cable powers the Pi and carries the data. Never use both ports at once.
2. After about 40 s, Windows finds a game controller. Open it with `Win+R` → `joy.cpl`.
   It is listed as **POKKEN CONTROLLER**; click Properties.
3. Run `windows\SwitchPadBridge.exe --pi <IP> --test`. (If the .exe is missing, run
   `build.cmd` first.)
   - The status line should show `Wi-Fi online` and `Switch: connected`.
     "Switch" here means whatever the Pi's USB port is plugged into, so the PC counts.
   - In joy.cpl, L+R light up three times, then the D-pad moves, then the left stick
     draws a circle.

## 6. On the Switch

1. On the Switch: System Settings → Controllers and Sensors →
   **Pro Controller Wired Communication: On**
2. Connect the Pi's **USB** port to a USB port on the dock. Wait about 40 s for it to boot.
3. On the Switch, open Controllers → **Change Grip/Order**.
4. On the PC, run `SwitchPadBridge.exe --pi <IP> --test`. The L+R presses register the
   controller.

**If the Switch never sees the Pi** but the PC does: force full-speed USB, which is what
the real HORI controller uses.
```
sudo touch /boot/firmware/switchpad/fullspeed && sudo systemctl restart switchpad
```
Delete that file to go back.

## 7. Playing over Parsec

1. Parsec → Settings → Host → **Virtual Gamepad Type: Xbox 360**.
2. Once the guest is connected, run `SwitchPadBridge.exe --pi <IP>`.
   - It reads the **highest connected XInput slot**, which is normally Parsec's pad.
     If it picks your own controller, run `SwitchPadBridge.exe --list` to see the slots
     and pass `--pad N`.
   - Leave the window open while playing. Ctrl+C releases every button and quits.

### Button mapping (default `--layout position`)

| Xbox (guest) | Switch |
|---|---|
| A / B / X / Y | B / A / Y / X (same physical spot) |
| LB / RB | L / R |
| LT / RT | ZL / ZR |
| Back / Start | − / + |
| Stick clicks | Stick clicks |
| Guide, or Back+Start | HOME |
| Back + right-stick click | Capture |

Use `--layout label` if you want the letters to match instead (Xbox A → Switch A).

## Wi-Fi lag and hiccups

The status line shows **`Ping: N ms`**: the round trip PC → Pi → PC. The delay our part
adds is about half of that; a few ms is normal. If the guest still feels a big delay
while Ping stays low, the lag is in Parsec, the capture card or the guest's own internet.

If Ping jumps around or input stops for a moment:

1. **Use a router instead of a laptop hotspot.** A hotspot shares the laptop's one Wi-Fi
   card with the laptop's own internet connection and with Parsec's video stream.
2. **Put the PC running Parsec on Ethernet** if you can. Then only the Pi uses Wi-Fi.
3. **Move the Pi closer to the router**, or at least out from behind the TV and the dock.
   The Pi's log shows its signal: around -60 dBm or better is good.
4. **Keep USB 3 cables and ports away from the Pi**: they interfere with 2.4 GHz.

**Failsafe:** if the PC stops sending for 0.5 s, the Pi releases every button, so a Wi-Fi
hiccup can't leave a button held down.

## Logs: collect everything in one go

Both sides log everything automatically:
- **PC:** `SwitchPadBridge.exe` writes `windows\logs\bridge-<date>-<time>.log`: when the Pi
  answers and stops answering, which XInput slots exist, every button press, and a stats
  line every 10 s (packets, Ping average and maximum).
- **Pi:** `switchpad.log` on the SD card, the journal, and the HDMI screen: USB state
  changes, every Wi-Fi hiccup longer than 0.3 s, lost packets, Wi-Fi signal and speed,
  power problems (under-voltage), and a stats line every 10 s. `pi delay` there is how
  long an input waits on the Pi before the Switch takes it.

To send them, play until the problem happens, close `SwitchPadBridge.exe`, then run:
```
windows\collect-logs.cmd USER@<Pi's IP>
```
It pings the Pi for 30 s, asks it for a full report over SSH, fetches its complete log,
and packs everything into **one zip file** in `windows\logs\`. Send that file. Without
SSH, unplug the Pi, put its SD card in the PC and run `collect-logs.cmd` with no argument.

## Updating the Pi (no re-flash)

**Over SSH** (Pi running and on Wi-Fi):
```
windows\update-pi.cmd USER@<Pi's IP>
```
It copies the new files, re-runs the install and restarts the bridge.

**Without SSH:** shut the Pi down by unplugging it, put the SD card in the PC, and
double-click `windows\prepare-sd.cmd` again. A card installed with the current version only
gets the new files. A card from an older version (including the Bluetooth one) gets the
install again: the Pi redoes its setup on the next boot and reboots once, which takes
about 3 minutes.

## Troubleshooting

| Message in SwitchPadBridge | Meaning |
|---|---|
| `looking up switchpad.local...` | The name lookup is slow or failing. Start with `--pi <IP>` instead (step 4). |
| `no reply from <IP>` | The Pi is off, still booting, or not on the same network. |
| `Switch: not connected` | Check the cable is in the Pi's **USB** port, that it's a data cable, and that the dock has power. |

With SSH: the service log is `journalctl -u switchpad -f`, and
`sudo python3 /boot/firmware/switchpad/switchpad.py status` shows whether the Switch is
reading the pad.
