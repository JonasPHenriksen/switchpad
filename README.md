# SwitchPad

Turn a **Raspberry Pi Zero W** into a wired Nintendo Switch controller that is driven from a
Windows PC over **Wi-Fi**. Made for playing a Switch remotely over Parsec: the guest's
controller shows up on the host PC as a virtual Xbox pad, and SwitchPad forwards it to the
Switch.

```
Parsec guest pad -> Parsec virtual Xbox pad (PC) -> SwitchPadBridge.exe
   -> Wi-Fi -> Pi Zero W -> its "USB" port -> Switch dock
Switch HDMI -> capture card -> PC -> Parsec
```

- The Pi appears on the Switch as a plain wired pad (HORI Pokken style HID): all buttons and
  sticks, no motion controls, rumble or amiibo.
- The Pi software installs itself on first boot. No keyboard or monitor is needed.
- The PC side is a single small program (`SwitchPadBridge.exe`), built with the C# compiler
  that ships with Windows. Nothing to install.
- Both sides log everything (connection hiccups, lost packets, round-trip time), and
  `collect-logs.cmd` packs it into one zip.

**Bluetooth:** we tried Bluetooth between the PC and the Pi first. It kept disconnecting
every few seconds, and it simply isn't worth the effort to make it work properly, so
SwitchPad is Wi-Fi only.

## Getting started

See **[SETUP.md](SETUP.md)** for the full step-by-step guide.

| Folder | What's in it |
|---|---|
| `pi/` | `switchpad.py` (the bridge on the Pi), `install.sh`, `firstboot.sh` |
| `windows/` | `SwitchPadBridge.cs` + `build.cmd`, and the helper scripts: `prepare-sd`, `check-sd`, `collect-logs`, `update-pi` |
