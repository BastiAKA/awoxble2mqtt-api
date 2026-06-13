#!/usr/bin/env python3
"""Generic BLE advertisement recorder (BlueZ D-Bus) for reverse-engineering an unknown lamp.

Unlike adv_watch.py (which assumes the known Connect-Z 0x0160 layout), this makes NO assumptions: for
each target MAC it dumps EVERY advertisement field — manufacturer data per company id, service data per
UUID, advertised service UUIDs, local name, tx power — and prints a CHANGE line whenever any of them
changes, plus a periodic heartbeat. Drive the lamp in the app and watch which bytes track which action;
if nothing changes, the lamp doesn't broadcast its state (and we need the GATT/btsnoop path instead).

Passive scan, no connection/login. Linux/BlueZ. Resolves the adapter each tick so it survives the Pi
dongle's hci0 ⇄ hci1 re-enumeration.

Usage: python3 adv_record.py <MAC> [MAC ...] [--seconds N]
"""
import sys, time
import dbus, dbus.mainloop.glib
from gi.repository import GLib

args = [a for a in sys.argv[1:]]
seconds = 600
if "--seconds" in args:
    i = args.index("--seconds")
    seconds = int(args[i + 1]); del args[i:i + 2]
TARGETS = {a.upper().replace("-", ":") for a in args}
if not TARGETS:
    print("usage: adv_record.py <MAC> [MAC ...] [--seconds N]"); sys.exit(2)
FRAGS = {t.replace(":", "_") for t in TARGETS}  # BlueZ path form, e.g. A4_C1_38_20_29_91

dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
bus = dbus.SystemBus()
last_sig = {}
last_seen = {}


def ts():
    return time.strftime("%H:%M:%S")


def find_adapter():
    om = dbus.Interface(bus.get_object("org.bluez", "/"), "org.freedesktop.DBus.ObjectManager")
    for path, ifaces in om.GetManagedObjects().items():
        if "org.bluez.Adapter1" in ifaces:
            return path
    return None


def b2h(v):
    return bytes(v).hex().upper()


def describe(dev):
    """Build a stable, human-readable snapshot of every advert field (RSSI excluded — too noisy)."""
    parts = []
    name = dev.get("Name") or dev.get("Alias")
    if name:
        parts.append(f"name={name}")
    if "TxPower" in dev:
        parts.append(f"tx={int(dev['TxPower'])}")
    md = dev.get("ManufacturerData")
    if md:
        for cid in sorted(md):
            parts.append(f"mfr[{int(cid):#06x}]={b2h(md[cid])}")
    sd = dev.get("ServiceData")
    if sd:
        for uuid in sorted(sd):
            parts.append(f"svc[{uuid}]={b2h(sd[uuid])}")
    uuids = dev.get("UUIDs")
    if uuids:
        parts.append("uuids=" + ",".join(sorted(str(u) for u in uuids)))
    return "  ".join(parts) if parts else "(no advert fields)"


def which(path):
    up = path.upper()
    for frag in FRAGS:
        if "DEV_" + frag in up:
            return frag
    return None


def scan_once():
    om = dbus.Interface(bus.get_object("org.bluez", "/"), "org.freedesktop.DBus.ObjectManager")
    for path, ifaces in om.GetManagedObjects().items():
        dev = ifaces.get("org.bluez.Device1")
        if dev is None:
            continue
        frag = which(path)
        if frag is None:
            continue
        sig = describe(dev)
        last_seen[frag] = (sig, dev.get("RSSI"))
        if last_sig.get(frag) != sig:
            last_sig[frag] = sig
            print(f"[{ts()}] CHANGE {frag.replace('_', ':')}  rssi={dev.get('RSSI')}  {sig}", flush=True)
    return True


def heartbeat():
    for frag, (sig, rssi) in last_seen.items():
        print(f"[{ts()}] hold   {frag.replace('_', ':')}  rssi={rssi}  {sig}", flush=True)
    if not last_seen:
        print(f"[{ts()}] hold   (none of the targets seen yet)", flush=True)
    return True


def ensure_discovery():
    ad = find_adapter()
    if ad is None:
        return
    a = dbus.Interface(bus.get_object("org.bluez", ad), "org.bluez.Adapter1")
    props = dbus.Interface(bus.get_object("org.bluez", ad), "org.freedesktop.DBus.Properties")
    try:
        if not bool(props.Get("org.bluez.Adapter1", "Discovering")):
            try:
                a.SetDiscoveryFilter(dbus.Dictionary(
                    {"Transport": dbus.String("le"), "DuplicateData": dbus.Boolean(True)}, signature="sv"))
            except dbus.DBusException:
                pass
            a.StartDiscovery()
    except dbus.DBusException:
        pass


def tick():
    ensure_discovery()
    try:
        scan_once()
    except dbus.DBusException:
        pass
    return True


print(f"recording {', '.join(sorted(TARGETS))} for {seconds}s — drive the lamp in the app now...", flush=True)
GLib.timeout_add_seconds(2, tick)
GLib.timeout_add_seconds(10, heartbeat)
loop = GLib.MainLoop()
GLib.timeout_add_seconds(seconds, lambda: (loop.quit(), False)[1])
ensure_discovery()
loop.run()
print("done", flush=True)
