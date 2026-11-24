#!/usr/bin/env python3
"""SwitchPad - turns a Raspberry Pi Zero W into a wired Nintendo Switch controller.

The Pi presents itself over its "USB" port as a simple wired pad (HORI Pokken
style HID, no motion/rumble). Button/stick states arrive from SwitchPad.exe /
SwitchPadBridge.exe on the Windows PC over the network (Wi-Fi or Ethernet, UDP port 5005).
The Pi answers with its status, its health (temperature, power, network, USB) and
the results of commands from the PC (self-test, restart, reboot).

Commands (run as root):
  switchpad.py run        set up the USB gadget and forward input (systemd uses this)
  switchpad.py selftest   press L+R and move the stick locally, no PC needed
                          (stop the service first; the PC's "Run self-test" button doesn't need that)
  switchpad.py status     show whether the Switch has the controller attached
  switchpad.py report     collect all diagnostics into report.txt next to this file

Logs: switchpad.log next to this file (on the SD card), the journal, and the HDMI screen.
An empty file named 'fullspeed' next to this file makes the USB side full-speed only.
"""
import errno
import math
import os
import queue
import re
import select
import signal
import socket
import struct
import subprocess
import sys
import threading
import time
import traceback

VERSION = "2026-11-24"   # shown in SwitchPad.exe's Pi tab

GADGET = "/sys/kernel/config/usb_gadget/switchpad"
HIDG = "/dev/hidg0"
UDP_PORT = 5005
MAGIC_IN = b"SWP1"       # PC -> Pi: magic(4) + seq uint32 LE(4) + report(8) = 16 bytes
MAGIC_STATUS = b"SWS1"   # Pi -> PC: magic(4) + switch_attached(1) + version 1 (1) + pad(2)
                         # + last seq received uint32 LE + ms since it arrived uint16 LE + pad(2)
                         # = 16 bytes. The echo lets the PC measure the link's round-trip time.
                         # Unchanged since v1, so older PC software keeps working.
MAGIC_HEALTH = b"SWH1"   # Pi -> PC every 2 s: magic + UTF-8 "key=value" lines (see health_msg)
MAGIC_CMD = b"SWC1"      # PC -> Pi: magic + nonce uint32 LE + command text (ping, selftest, restart, reboot)
MAGIC_REPLY = b"SWR1"    # Pi -> PC: magic + that nonce + reply text, starting "ok", "done" or "error"
FAILSAFE_S = 0.5         # release everything if the PC goes quiet this long
GAP_S = 0.15             # the PC sends at least every 50 ms; a longer silence is a network hiccup
SELFTEST_S = 12.0        # length of the self-test pattern (same as the PC's test pattern)

# HID report descriptor of a HORI Pokken-style wired pad (the Switch accepts it natively).
# Input report (8 bytes): buttons uint16, hat (low nibble), LX, LY, RX, RY, vendor byte.
REPORT_DESC = bytes([
    0x05, 0x01,        # Usage Page (Generic Desktop)
    0x09, 0x05,        # Usage (Game Pad)
    0xA1, 0x01,        # Collection (Application)
    0x15, 0x00,        #   Logical Minimum (0)
    0x25, 0x01,        #   Logical Maximum (1)
    0x35, 0x00,        #   Physical Minimum (0)
    0x45, 0x01,        #   Physical Maximum (1)
    0x75, 0x01,        #   Report Size (1)
    0x95, 0x10,        #   Report Count (16)
    0x05, 0x09,        #   Usage Page (Button)
    0x19, 0x01,        #   Usage Minimum (1)
    0x29, 0x10,        #   Usage Maximum (16)
    0x81, 0x02,        #   Input (Data,Var,Abs)
    0x05, 0x01,        #   Usage Page (Generic Desktop)
    0x25, 0x07,        #   Logical Maximum (7)
    0x46, 0x3B, 0x01,  #   Physical Maximum (315)
    0x75, 0x04,        #   Report Size (4)
    0x95, 0x01,        #   Report Count (1)
    0x65, 0x14,        #   Unit (Degrees)
    0x09, 0x39,        #   Usage (Hat Switch)
    0x81, 0x42,        #   Input (Data,Var,Abs,Null)
    0x65, 0x00,        #   Unit (None)
    0x95, 0x01,        #   Report Count (1)
    0x81, 0x01,        #   Input (Const) - 4 bit padding
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x46, 0xFF, 0x00,  #   Physical Maximum (255)
    0x09, 0x30,        #   Usage (X)
    0x09, 0x31,        #   Usage (Y)
    0x09, 0x32,        #   Usage (Z)
    0x09, 0x35,        #   Usage (Rz)
    0x75, 0x08,        #   Report Size (8)
    0x95, 0x04,        #   Report Count (4)
    0x81, 0x02,        #   Input (Data,Var,Abs)
    0x06, 0x00, 0xFF,  #   Usage Page (Vendor Defined 0xFF00)
    0x09, 0x20,        #   Usage (0x20)
    0x95, 0x01,        #   Report Count (1)
    0x81, 0x02,        #   Input (Data,Var,Abs)
    0x0A, 0x21, 0x26,  #   Usage (0x2621)
    0x95, 0x08,        #   Report Count (8)
    0x91, 0x02,        #   Output (Data,Var,Abs)
    0xC0,              # End Collection
])

# Button bits in the report
Y, B, A, X, L, R, ZL, ZR, MINUS, PLUS, LCLICK, RCLICK, HOME, CAPTURE = [1 << i for i in range(14)]
HAT_CENTER = 8


def make_report(buttons=0, hat=HAT_CENTER, lx=128, ly=128, rx=128, ry=128):
    return struct.pack("<HBBBBBB", buttons, hat, lx, ly, rx, ry, 0)


NEUTRAL = make_report()


def test_pattern(t):
    """The test pattern at t seconds from its start (same as TestScript in SwitchPadBridge.cs):
    L+R three times (registers the pad on 'Change Grip/Order'), D-pad right and left, then a
    left stick circle. Avoids A so it can't launch anything."""
    if t < 1.0:
        return NEUTRAL
    if t < 5.5:
        return make_report(L | R) if (t - 1.0) % 1.5 < 0.5 else NEUTRAL
    if t < 6.0:
        return make_report(hat=2)
    if t < 6.5:
        return NEUTRAL
    if t < 7.0:
        return make_report(hat=6)
    if t < 8.0:
        return NEUTRAL
    if t < 11.0:
        a = (t - 8.0) * 2 * math.pi
        return make_report(lx=int(128 + 127 * math.cos(a)), ly=int(128 - 127 * math.sin(a)))
    return NEUTRAL


# Log file on the SD card's boot partition, so it can be read from Windows without SSH.
LOG_FILE = None
for _d in ("/boot/firmware/switchpad", "/boot/switchpad"):
    if os.path.isdir(_d):
        LOG_FILE = os.path.join(_d, "switchpad.log")
        break
_log_lock = threading.Lock()


def uptime():
    try:
        with open("/proc/uptime") as f:
            return float(f.read().split()[0])
    except (OSError, ValueError):
        return 0.0


_log_queue = None  # set by start_log_thread(): the main loop then never waits for the SD card


def _write_lines(lines):
    with _log_lock:
        if LOG_FILE:
            try:
                with open(LOG_FILE, "a") as f:
                    f.write("".join(l + "\n" for l in lines))
                    f.flush()
                    os.fsync(f.fileno())  # the Pi is switched off by unplugging: write it now
            except OSError:
                pass
        try:
            with open("/dev/tty1", "w") as tty:  # the login screen on HDMI
                tty.write("".join("switchpad " + l + "\r\n" for l in lines))
        except OSError:
            pass


def _log_writer():
    while True:
        lines = [_log_queue.get()]
        while True:
            try:
                lines.append(_log_queue.get_nowait())
            except queue.Empty:
                break
        _write_lines(lines)


def start_log_thread():
    global _log_queue
    _log_queue = queue.Queue()
    threading.Thread(target=_log_writer, daemon=True).start()


def log(msg, sync=False):
    """Log to the journal, to switchpad.log on the SD card, and onto the HDMI screen.

    Writing to the SD card takes ~50 ms on a Pi Zero, so while the bridge runs that happens on
    a background thread. sync=True writes immediately (crash and shutdown messages)."""
    print(msg, flush=True)
    if os.geteuid() != 0:
        return
    stamp = "[%7.1f s after boot] %s" % (uptime(), msg)
    if _log_queue is not None and not sync:
        _log_queue.put(stamp)
        return
    lines = []
    while _log_queue is not None:  # write what's still queued first, keeping the order
        try:
            lines.append(_log_queue.get_nowait())
        except queue.Empty:
            break
    _write_lines(lines + [stamp])


def rotate_log():
    try:
        if LOG_FILE and os.path.getsize(LOG_FILE) > 256 * 1024:
            os.replace(LOG_FILE, LOG_FILE + ".old")
    except OSError:
        pass


def write_attr(path, value):
    mode = "wb" if isinstance(value, bytes) else "w"
    with open(path, mode) as f:
        f.write(value)


def read_attr(path):
    try:
        with open(path) as f:
            return f.read().strip()
    except OSError:
        return ""


# ---------------------------------------------------------------- USB gadget

def setup_gadget():
    """Create the USB HID gadget via configfs (idempotent)."""
    subprocess.run(["modprobe", "libcomposite"], check=False)
    if os.path.isdir(GADGET):
        udc = read_attr(os.path.join(GADGET, "UDC"))
        if udc:
            # Already bound (service restart). Re-bind only if the wanted speed changed.
            if read_attr(os.path.join(GADGET, "max_speed")) not in ("", wanted_usb_speed()):
                write_attr(os.path.join(GADGET, "UDC"), "")
                apply_usb_speed()
                write_attr(os.path.join(GADGET, "UDC"), udc)
                log("USB gadget re-bound with max speed %s" % wanted_usb_speed())
            return
    else:
        g = lambda rel: os.path.join(GADGET, rel)
        os.makedirs(GADGET)
        write_attr(g("idVendor"), "0x0f0d")   # HORI
        write_attr(g("idProduct"), "0x0092")  # Pokken Tournament controller
        write_attr(g("bcdDevice"), "0x0572")
        write_attr(g("bcdUSB"), "0x0200")
        os.makedirs(g("strings/0x409"))
        write_attr(g("strings/0x409/manufacturer"), "HORI CO.,LTD.")
        write_attr(g("strings/0x409/product"), "POKKEN CONTROLLER")
        write_attr(g("strings/0x409/serialnumber"), "SWITCHPAD0001")

        func = g("functions/hid.usb0")
        os.makedirs(func)
        write_attr(os.path.join(func, "protocol"), "0")
        write_attr(os.path.join(func, "subclass"), "0")
        write_attr(os.path.join(func, "report_length"), "64")
        write_attr(os.path.join(func, "report_desc"), REPORT_DESC)

        cfg = g("configs/c.1")
        os.makedirs(os.path.join(cfg, "strings/0x409"))
        write_attr(os.path.join(cfg, "strings/0x409/configuration"), "SwitchPad")
        write_attr(os.path.join(cfg, "MaxPower"), "500")
        write_attr(os.path.join(cfg, "bmAttributes"), "0x80")
        os.symlink(func, os.path.join(cfg, "hid.usb0"))

    udcs = sorted(os.listdir("/sys/class/udc")) if os.path.isdir("/sys/class/udc") else []
    if not udcs:
        raise RuntimeError("No USB device controller found. Check that config.txt has "
                           "'dtoverlay=dwc2,dr_mode=peripheral' and that you rebooted.")
    apply_usb_speed()
    write_attr(os.path.join(GADGET, "UDC"), udcs[0])
    log("USB gadget bound to %s (max speed: %s)"
        % (udcs[0], read_attr(os.path.join(GADGET, "max_speed")) or "default"))


def wanted_usb_speed():
    """An empty file named 'fullspeed' next to switchpad.py on the SD card forces full-speed USB
    (what the real HORI pad uses). Helps with hubs/docks that don't like the Pi's high-speed."""
    if LOG_FILE and os.path.exists(os.path.join(os.path.dirname(LOG_FILE), "fullspeed")):
        return "full-speed"
    return "high-speed"


def apply_usb_speed():
    """Set max_speed on the (unbound) gadget. Returns True if it changed."""
    path = os.path.join(GADGET, "max_speed")
    want = wanted_usb_speed()
    if os.path.exists(path) and read_attr(path) != want:
        try:
            write_attr(path, want)
            return True
        except OSError as e:
            log("Could not set USB max speed to %s: %s" % (want, e))
    return False


def usb_state():
    udc = read_attr(os.path.join(GADGET, "UDC"))
    if not udc:
        return "gadget not bound"
    return read_attr("/sys/class/udc/%s/state" % udc) or "unknown"


def usb_speed():
    udc = read_attr(os.path.join(GADGET, "UDC"))
    return read_attr("/sys/class/udc/%s/current_speed" % udc) if udc else ""


def switch_attached():
    return usb_state() == "configured"


# ---------------------------------------------------------------- health readings

THROTTLE_BITS = {0: "UNDER-VOLTAGE NOW", 1: "freq capped now", 2: "throttled now",
                 3: "soft temp limit now", 16: "under-voltage since boot",
                 17: "freq capped since boot", 18: "throttled since boot",
                 19: "soft temp limit since boot"}


def throttled_value():
    """Pi power/heat flags: (raw value or None, text). Under-voltage makes the network and USB
    unreliable."""
    raw = read_attr("/sys/devices/platform/soc/soc:firmware/get_throttled")
    if not raw:
        try:
            out = subprocess.run(["vcgencmd", "get_throttled"], capture_output=True,
                                 text=True, timeout=3).stdout
            raw = out.strip().split("=")[-1]
        except (OSError, subprocess.TimeoutExpired):
            return None, "?"
    try:
        v = int(raw, 16)
    except ValueError:
        return None, raw
    flags = [name for bit, name in THROTTLE_BITS.items() if v & (1 << bit)]
    return v, "0x%x%s" % (v, (" (" + ", ".join(flags) + ")") if flags else " (ok)")


def throttled():
    return throttled_value()[1]


def wifi_level(dev="wlan0"):
    try:
        with open("/proc/net/wireless") as f:
            for line in f:
                if line.strip().startswith(dev + ":"):
                    return "%s dBm" % line.split()[3].rstrip(".")
    except OSError:
        pass
    return "off"


def wifi_link(dev="wlan0"):
    """Signal, speed and channel of the Pi's Wi-Fi connection, e.g. '-38 dBm, 72.2 MBit/s, 2462 MHz'."""
    try:
        out = subprocess.run(["iw", "dev", dev, "link"], capture_output=True, text=True,
                             timeout=2).stdout
    except (OSError, subprocess.TimeoutExpired):
        return wifi_level(dev)
    if "Not connected" in out or not out.strip():
        return "NOT CONNECTED"
    parts = []
    for pattern in (r"signal:\s*(-?\d+ dBm)", r"tx bitrate:\s*([\d.]+ MBit/s)", r"freq:\s*([\d.]+)"):
        m = re.search(pattern, out)
        if m:
            parts.append(m.group(1) + (" MHz" if "freq" in pattern else ""))
    return ", ".join(parts) or wifi_level(dev)


def net_link(peer_ip=None):
    """The network connection the PC's packets use: (device, kind, description, connected).
    kind is 'ethernet', 'wifi', 'other' or 'none'. A Pi 4 on a cable reports Ethernet, not a
    missing Wi-Fi connection."""
    dev = None
    if peer_ip:
        try:
            out = subprocess.run(["ip", "route", "get", peer_ip], capture_output=True, text=True,
                                 timeout=2).stdout
            m = re.search(r"\bdev (\S+)", out)
            if m:
                dev = m.group(1)
        except (OSError, subprocess.TimeoutExpired):
            pass
    if dev is None:  # no PC yet: whichever connection is there
        if read_attr("/sys/class/net/eth0/carrier") == "1":
            dev = "eth0"
        elif os.path.isdir("/sys/class/net/wlan0"):
            dev = "wlan0"
        else:
            return None, "none", "no network connection", False
    if dev.startswith(("eth", "en")):
        if read_attr("/sys/class/net/%s/carrier" % dev) != "1":
            return dev, "ethernet", "Ethernet: NO CABLE / NO LINK", False
        desc = "Ethernet"
        speed = read_attr("/sys/class/net/%s/speed" % dev)
        duplex = read_attr("/sys/class/net/%s/duplex" % dev)
        if speed.isdigit():
            desc += " %s Mbit/s" % speed
        if duplex in ("full", "half"):
            desc += ", %s duplex" % duplex
        return dev, "ethernet", desc, True
    if dev.startswith(("wlan", "wl")):
        w = wifi_link(dev)
        connected = w != "NOT CONNECTED"
        return dev, "wifi", "Wi-Fi " + w, connected
    return dev, "other", dev, read_attr("/sys/class/net/%s/operstate" % dev) == "up"


def cpu_temp():
    try:
        return "%.1f" % (int(read_attr("/sys/class/thermal/thermal_zone0/temp")) / 1000.0)
    except ValueError:
        return ""


def loadavg():
    return (read_attr("/proc/loadavg").split() or [""])[0]


def pi_model():
    try:
        with open("/proc/device-tree/model", "rb") as f:
            return f.read().decode("ascii", "replace").strip("\x00 \n")
    except OSError:
        return "?"


PI_MODEL = pi_model()

# Slow readings (a vcgencmd/iw/ip call takes tens of ms on a Pi Zero) are taken on a background
# thread, so the bridge loop never waits for them.
health = {"power": "?", "power_raw": "", "link": "?", "link_kind": "", "dev": "", "temp": "",
          "load": "", "selftest": "", "peer": None}


def health_loop():
    last = None  # (device, connected)
    while True:
        try:
            raw, text = throttled_value()
            health["power"] = text
            health["power_raw"] = "0x%x" % raw if raw is not None else ""
            dev, kind, desc, connected = net_link(health["peer"])
            health.update(dev=dev or "", link_kind=kind, link=desc)
            health["temp"] = cpu_temp()
            health["load"] = loadavg()
            if last is not None and (dev, connected) != last:
                if dev != last[0]:
                    log("Network: now using %s (%s)" % (desc, dev))
                elif connected:
                    log("Network: %s connected again: %s" % (dev, desc))
                else:
                    log("Network: %s connection LOST (%s)" % (dev, desc))
            last = (dev, connected)
        except Exception as e:  # never let a reading crash anything
            health["link"] = "error %r" % e
        time.sleep(5)


BUTTON_NAMES = ["Y", "B", "A", "X", "L", "R", "ZL", "ZR", "-", "+", "LS", "RS", "HOME", "CAPTURE"]


def describe(report):
    b, hat = struct.unpack_from("<HB", report)
    names = [n for i, n in enumerate(BUTTON_NAMES) if b & (1 << i)]
    if hat < 8:
        names.append("Dpad%d" % hat)
    return " ".join(names) or "-"


# ---------------------------------------------------------------- main loop

class Bridge:
    def __init__(self):
        self.hid = None
        self.hid_retry_at = 0.0
        self.latest = NEUTRAL
        self.dirty = True
        self.last_rx = 0.0
        self.peer = None
        self.dirty_since = time.monotonic()  # when the oldest not-yet-written change arrived
        # counters: n is reset with every stats line, total counts since the start (Pi tab on the PC)
        self.n = dict(rx=0, lost=0, late=0, gaps=0, changes=0, hid_ok=0, hid_err=0, failsafe=0)
        self.total = dict(self.n)
        self.delay_sum = self.delay_max = self.gap_max = 0.0
        self.last_hid_err = None
        self.selftest_until = 0.0  # while a self-test runs, the PC's input is not used

    def count(self, key, k=1):
        self.n[key] += k
        self.total[key] += k

    def set_report(self, report, now):
        if not self.dirty:
            self.dirty_since = now
        self.latest, self.dirty = report, True

    def got_report(self, report, now):
        self.last_rx = now
        if now < self.selftest_until:
            return
        if report != self.latest:
            # Not logged one by one: an SD-card write per button press would add input lag.
            # The stats line counts them; SwitchPadBridge's log on the PC has every press.
            self.set_report(report, now)
            self.count("changes")


def try_setup_gadget():
    try:
        setup_gadget()
        return True
    except Exception as e:
        log("USB gadget setup failed: %r (will retry)" % e)
        return False


def run():
    rotate_log()
    log("=== SwitchPad %s starting (%s, Python %s, %s, power: %s, network: %s) ==="
        % (VERSION, os.path.abspath(__file__), sys.version.split()[0], PI_MODEL, throttled(),
           net_link()[2]))
    start_log_thread()
    threading.Thread(target=health_loop, daemon=True).start()

    def on_term(*_):
        log("Stopping (service stopped or Pi shutting down)", sync=True)
        sys.exit(0)
    signal.signal(signal.SIGTERM, on_term)

    started = time.monotonic()
    gadget_ok = try_setup_gadget()
    gadget_next_try = time.monotonic() + 5.0
    br = Bridge()

    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    udp.bind(("", UDP_PORT))
    udp.setblocking(False)
    log("Listening for SwitchPad on UDP port %d" % UDP_PORT)
    last_seq = None
    udp_err_logged = False
    status_err = 0

    def status_msg(now):
        """Status for the PC, echoing the newest input so it can measure the round-trip time."""
        if last_seq is None:
            seq, age = 0xFFFFFFFF, 0
        else:
            seq, age = last_seq, min(65535, int((now - br.last_rx) * 1000))
        return (MAGIC_STATUS + bytes([1 if last_usb == "configured" else 0, 1, 0, 0])
                + struct.pack("<IHH", seq, age, 0))

    def health_msg(now):
        """Everything for the PC's Pi tab, as UTF-8 'key=value' lines."""
        tot = br.total
        fields = [
            ("v", 1), ("sw", VERSION), ("model", PI_MODEL), ("host", socket.gethostname()),
            ("uptime", int(uptime())), ("running", int(now - started)),
            ("temp", health["temp"]), ("power", health["power_raw"]), ("power_text", health["power"]),
            ("link", health["link_kind"]), ("link_text", health["link"]), ("dev", health["dev"]),
            ("usb", last_usb), ("usb_speed", usb_speed()), ("load", health["load"]),
            ("rx", tot["rx"]), ("lost", tot["lost"]), ("late", tot["late"]),
            ("hiccups", tot["gaps"]), ("failsafe", tot["failsafe"]), ("usb_err", tot["hid_err"]),
            ("selftest", "running" if selftest else health["selftest"]),
        ]
        text = "".join("%s=%s\n" % (k, str(v).replace("\n", " ")) for k, v in fields)
        return MAGIC_HEALTH + text.encode("utf-8")

    # ---- commands from the PC (SwitchPad.exe's Pi tab) ----
    recent_cmds = {}  # nonce -> reply: a resent command gets the same answer, not a second run
    selftest = None   # the running self-test: dict(nonce, addr, ok0, err0, start)

    def reply(nonce, text, addr):
        try:
            udp.sendto(MAGIC_REPLY + struct.pack("<I", nonce) + text.encode("utf-8")[:900], addr)
        except OSError:
            pass

    def handle_command(data, addr, now):
        nonlocal selftest
        nonce = struct.unpack_from("<I", data, 4)[0]
        cmd = data[8:].decode("utf-8", "replace").strip().lower()
        if nonce in recent_cmds:
            reply(nonce, recent_cmds[nonce], addr)
            return
        # Same trust as the input itself: only the PC that is driving the controller.
        if addr != br.peer:
            reply(nonce, "error: commands are only taken from the PC that is sending input", addr)
            return
        after = None
        if cmd == "ping":
            text = "ok: pong (SwitchPad %s on %s)" % (VERSION, PI_MODEL)
        elif cmd == "selftest":
            if selftest:
                text = "error: a self-test is already running"
            elif br.hid is None or last_usb != "configured":
                text = ("error: the Switch isn't using the controller (USB state: %s), so there "
                        "is nothing to test. Check the cable and that the Switch is docked and awake."
                        % last_usb)
            else:
                selftest = dict(nonce=nonce, addr=addr, ok0=br.total["hid_ok"],
                                err0=br.total["hid_err"], start=now)
                br.selftest_until = now + SELFTEST_S
                text = ("ok: self-test running for %.0f s: L+R three times, D-pad right and left, "
                        "left stick circle. The PC's input is paused meanwhile." % SELFTEST_S)
        elif cmd == "restart":
            text = "ok: restarting SwitchPad on the Pi (back in about 5 s)"
            after = ["systemctl", "--no-block", "restart", "switchpad"]
        elif cmd == "reboot":
            text = "ok: rebooting the Pi (back in about 30-60 s)"
            after = ["systemctl", "reboot"]
        else:
            text = "error: unknown command %r (this Pi runs SwitchPad %s)" % (cmd, VERSION)
        recent_cmds[nonce] = text
        while len(recent_cmds) > 32:
            del recent_cmds[next(iter(recent_cmds))]
        log("Command from %s: %s -> %s" % (addr[0], cmd, text), sync=after is not None)
        reply(nonce, text, addr)
        if after:
            try:
                subprocess.Popen(after)
            except OSError as e:
                msg = "error: %s failed: %s" % (" ".join(after), e)
                recent_cmds[nonce] = msg
                log(msg)
                reply(nonce, msg, addr)

    def finish_selftest(now):
        nonlocal selftest
        st, selftest = selftest, None
        br.selftest_until = 0.0
        br.set_report(NEUTRAL, now)
        ok = br.total["hid_ok"] - st["ok0"]
        err = br.total["hid_err"] - st["err0"]
        good = ok > 0 and err == 0 and last_usb == "configured"
        text = ("done: self-test %s: %d reports handed to the Switch, %d USB write errors, USB state %s"
                % ("OK" if good else "FAILED", ok, err, last_usb))
        health["selftest"] = "%s (%s)" % (text[6:], time.strftime("%H:%M"))
        log(text)
        recent_cmds[st["nonce"]] = text
        reply(st["nonce"], text, st["addr"])

    last_status = last_health = 0.0
    last_usb, last_usb_check = None, 0.0
    last_stats = time.monotonic()
    loop_max, prev_iter = 0.0, time.monotonic()

    while True:
        now = time.monotonic()
        loop_max = max(loop_max, now - prev_iter)
        prev_iter = now

        if not gadget_ok and now >= gadget_next_try:
            gadget_ok = try_setup_gadget()
            gadget_next_try = now + 5.0
        if br.hid is None and gadget_ok and now >= br.hid_retry_at:
            try:
                br.hid = os.open(HIDG, os.O_RDWR | os.O_NONBLOCK)
                log("Opened %s" % HIDG)
            except OSError as e:
                br.hid_retry_at = now + 2.0
                if br.last_hid_err != "open":
                    log("Can't open %s yet: %s (retrying)" % (HIDG, e))
                    br.last_hid_err = "open"

        want_write = br.hid is not None and br.dirty and now >= br.hid_retry_at
        rlist = [udp] + ([br.hid] if br.hid is not None else [])
        wlist = [br.hid] if want_write else []
        timeout = 0.02
        if br.hid is not None and br.dirty and not want_write:
            timeout = min(timeout, max(0.0, br.hid_retry_at - now))  # retry the USB write on time
        r, w, _ = select.select(rlist, wlist, [], timeout)
        now = time.monotonic()

        # --- input and commands from the PC over the network ---
        if udp in r:
            while True:
                try:
                    data, addr = udp.recvfrom(1024)
                except BlockingIOError:
                    break
                except OSError as e:  # e.g. "connection reset" after the PC program closed
                    if not udp_err_logged:
                        log("Network receive error (ignored, logged once): %s" % e)
                        udp_err_logged = True
                    break
                if data[:4] == MAGIC_CMD and len(data) > 8:
                    handle_command(data, addr, now)
                    continue
                if len(data) != 16 or data[:4] != MAGIC_IN:
                    continue
                seq = struct.unpack_from("<I", data, 4)[0]
                quiet = now - br.last_rx
                if last_seq is not None and quiet < 1.0:
                    ahead = (seq - last_seq) & 0xFFFFFFFF
                    if ahead == 0 or ahead >= 0x80000000:  # duplicate, or older than one we used
                        br.count("late")
                        continue
                    br.count("lost", ahead - 1)  # packets that never arrived
                if last_seq is not None and GAP_S < quiet < 5.0:
                    # The PC sends at least every 50 ms, so this silence was a network hiccup.
                    br.count("gaps")
                    br.gap_max = max(br.gap_max, quiet)
                    if quiet >= 0.3:
                        log("Network hiccup: nothing arrived from the PC for %.0f ms" % (quiet * 1000))
                if addr != br.peer:
                    log("Receiving input from %s" % addr[0])
                    br.peer = addr
                    health["peer"] = addr[0]
                last_seq = seq
                br.count("rx")
                br.got_report(data[8:16], now)

        # --- self-test from the PC's Pi tab: the Pi plays the test pattern itself ---
        if selftest:
            if now >= br.selftest_until:
                finish_selftest(now)
            else:
                rep = test_pattern(now - selftest["start"])
                if rep != br.latest:
                    br.set_report(rep, now)

        # --- USB to the Switch ---
        if br.hid is not None and br.hid in r:
            try:
                os.read(br.hid, 64)  # drain output reports from the Switch (unused)
            except OSError:
                pass
        if br.hid is not None and br.hid in w and br.dirty:
            try:
                os.write(br.hid, br.latest)
                br.dirty = False
                br.count("hid_ok")
                d = now - br.dirty_since  # time from arriving at the Pi to handed to USB
                br.delay_sum += d
                br.delay_max = max(br.delay_max, d)
                if br.last_hid_err not in (None, "open"):
                    log("USB writes work again")
                br.last_hid_err = None
            except BlockingIOError:
                br.hid_retry_at = now + 0.002  # previous report not taken yet; don't spin
            except OSError as e:
                # Never sleep here: input would pile up and arrive late.
                br.count("hid_err")
                br.hid_retry_at = now + 0.25
                err = errno.errorcode.get(e.errno, str(e.errno))
                if err != br.last_hid_err:
                    log("USB write failed: %s (%s); keeps retrying every 0.25 s" % (err, e.strerror))
                    br.last_hid_err = err

        if br.latest != NEUTRAL and now - br.last_rx > FAILSAFE_S and not selftest:
            br.set_report(NEUTRAL, now)  # the PC went quiet: release all buttons
            br.count("failsafe")
            log("Failsafe: no input for %.1f s, released all buttons" % FAILSAFE_S)

        # --- USB state changes (not attached / default / addressed / configured / suspended) ---
        if now - last_usb_check >= 0.2:
            last_usb_check = now
            st = usb_state()
            if st != last_usb:
                log("USB state: %s -> %s%s" % (last_usb, st,
                    "  (the Switch/PC is using the controller)" if st == "configured" else ""))
                last_usb = st

        # --- status back to the PC, twice a second ---
        if now - last_status >= 0.5:
            last_status = now
            if br.peer is not None and now - br.last_rx < 5.0:
                try:
                    udp.sendto(status_msg(now), br.peer)
                except OSError:
                    status_err += 1

        # --- health for the PC's Pi tab, every 2 s (older PC software ignores it) ---
        if now - last_health >= 2.0:
            last_health = now
            if br.peer is not None and now - br.last_rx < 5.0:
                try:
                    udp.sendto(health_msg(now), br.peer)
                except OSError:
                    status_err += 1

        # --- stats line every 10 s ---
        if now - last_stats >= 10.0:
            n = br.n
            log("stats %.0fs: usb=%s | in: %d packets, lost %d, late %d, changes %d, last %s | "
                "hiccups >%.0f ms: %d, longest %.0f ms | usb writes ok=%d err=%d, "
                "pi delay avg %.1f max %.1f ms | failsafe=%d | status send err=%d | "
                "loop max %.0f ms | power %s | net %s | cpu %s C | holding: %s"
                % (now - last_stats, last_usb, n["rx"], n["lost"], n["late"], n["changes"],
                   ("%.1fs ago" % (now - br.last_rx)) if br.last_rx else "never",
                   GAP_S * 1000, n["gaps"], br.gap_max * 1000, n["hid_ok"], n["hid_err"],
                   br.delay_sum * 1000 / max(1, n["hid_ok"]), br.delay_max * 1000,
                   n["failsafe"], status_err, loop_max * 1000,
                   health["power"], health["link"], health["temp"] or "?", describe(br.latest)))
            for k in n:
                n[k] = 0
            br.delay_sum = br.delay_max = br.gap_max = 0.0
            status_err, loop_max, last_stats = 0, 0.0, now


def selftest():
    """Press L+R three times (registers the pad on 'Change Grip/Order'), then move things."""
    setup_gadget()
    fd = os.open(HIDG, os.O_RDWR)

    def send(report, hold):
        _, w, _ = select.select([], [fd], [], 2.0)
        if not w:
            log("The Switch isn't reading the controller. Is the cable in the Pi's 'USB' port "
                "(not 'PWR') and is it a data cable?")
            sys.exit(1)
        os.write(fd, report)
        time.sleep(hold)

    log("Self-test: open System Settings > Controllers > Change Grip/Order on the Switch.")
    for i in range(3):
        log("  L+R (%d/3)" % (i + 1))
        send(make_report(L | R), 0.5)
        send(NEUTRAL, 1.0)
    log("  D-pad right, left")
    send(make_report(hat=2), 0.3)
    send(NEUTRAL, 0.5)
    send(make_report(hat=6), 0.3)
    send(NEUTRAL, 0.5)
    log("  Left stick circle")
    for step in range(60):
        a = step / 60.0 * 2 * math.pi
        send(make_report(lx=int(128 + 127 * math.cos(a)), ly=int(128 - 127 * math.sin(a))), 0.03)
    send(NEUTRAL, 0.1)
    log("Self-test done. If the Switch showed the controller, the USB side works.")


# ---------------------------------------------------------------- full diagnostics report

REPORT_CMDS = [
    ("date / uptime", "date; uptime"),
    ("system", "uname -a; head -3 /etc/os-release; python3 --version"),
    ("power and temperature", "vcgencmd get_throttled; vcgencmd measure_volts core; vcgencmd measure_temp"),
    ("switchpad service", "systemctl status switchpad --no-pager -l | head -20"),
    ("switchpad journal, this boot", "journalctl -u switchpad -b --no-pager | tail -n 300"),
    ("switchpad journal, previous boot", "journalctl -u switchpad -b -1 --no-pager 2>&1 | tail -n 80"),
    ("Wi-Fi connection log (NetworkManager)",
     "journalctl -b -u NetworkManager --no-pager | grep -iE 'wlan0|wifi|state|disconnect|deauth|reason' "
     "| tail -n 80"),
    ("kernel messages: usb / wifi / power",
     "dmesg -T | grep -iE 'dwc2|usb|gadget|udc|brcmf|wlan|voltage|throttl' | tail -n 150"),
    ("usb gadget", "for f in /sys/class/udc/*/state /sys/class/udc/*/current_speed "
                   "/sys/kernel/config/usb_gadget/switchpad/{idVendor,idProduct,max_speed,UDC}; "
                   "do echo \"$f: $(cat $f 2>&1)\"; done; ls -l /dev/hidg* 2>&1; "
                   "ls /boot/firmware/switchpad/"),
    ("config.txt (dwc2, disable-bt) and cmdline.txt",
     "grep -nE 'dwc2|disable-bt|disable-wifi' /boot/firmware/config.txt; cat /boot/firmware/cmdline.txt"),
    ("network: addresses and routes", "ip -br addr; ip route"),
    ("ethernet", "for f in carrier speed duplex; do echo \"eth0 $f: $(cat /sys/class/net/eth0/$f 2>&1)\"; done; "
                 "ip -s link show eth0 2>&1 | head -8"),
    ("wifi", "iw dev wlan0 link; iw dev wlan0 get power_save; iw dev wlan0 station dump "
             "| grep -E 'signal|bitrate|retries|failed|beacon loss'; rfkill list"),
    ("switchpad.log on the card", "tail -n 200 /boot/firmware/switchpad/switchpad.log"),
    ("install.log on the card", "tail -n 40 /boot/firmware/switchpad/install.log"),
]


def report():
    """Collect everything useful into report.txt on the SD card (and print it)."""
    out_dir = os.path.dirname(LOG_FILE) if LOG_FILE else "/tmp"
    out_path = os.path.join(out_dir, "report.txt")
    parts = ["SwitchPad report, %s (software %s, %s)" % (time.strftime("%Y-%m-%d %H:%M:%S"), VERSION, PI_MODEL)]
    for title, cmd in REPORT_CMDS:
        try:
            res = subprocess.run(["bash", "-c", cmd], capture_output=True, text=True, timeout=20)
            text = (res.stdout + res.stderr).rstrip()
        except subprocess.TimeoutExpired:
            text = "(timed out)"
        parts.append("\n===== %s\n$ %s\n%s" % (title, cmd, text))
    data = "\n".join(parts) + "\n"
    with open(out_path, "w") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    print(data)
    print("Saved to %s" % out_path)


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "run"
    if os.geteuid() != 0:
        sys.exit("Run as root (sudo).")
    if cmd == "run":
        try:
            run()
        except SystemExit:
            raise
        except BaseException:
            log("CRASH:\n" + traceback.format_exc(), sync=True)
            raise
    elif cmd == "report":
        report()
    elif cmd == "selftest":
        selftest()
    elif cmd == "status":
        setup_gadget()
        print("Switch attached" if switch_attached() else "Switch NOT attached")
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
