#!/usr/bin/env python3
"""Feasibility probe: can this adapter/dongle hold MULTIPLE simultaneous central GATT links?

The AwoX backend today holds ONE session and disconnect+reconnects to switch mesh. Controlling lamps on
DIFFERENT meshes at once (e.g. Badezimmer on `mesh` + Badezimmer Spiegel on `zigbee`) needs >1 concurrent
connection. Before rearchitecting the connection layer, this answers the radio-level question directly:
connect to every MAC given, then report whether ALL show Connected=true at the same time.

It only does the BlueZ Device1.Connect() (link-layer + GATT) — NO AwoX login/session — which is enough to
test the controller's max simultaneous links. Passive, leaves the lamps' state untouched; disconnects all
on exit.

Usage: python3 multi_connect_test.py <MAC> <MAC> [MAC ...] [--hold N]
  --hold N   seconds to keep both links up while re-checking (default 10)

Linux/BlueZ only. Resolves the adapter each run so it survives the Pi dongle's hci0 <-> hci1 churn.
NOTE: the running awox-api does periodic LE discovery on the same adapter; if a connect aborts with
le-connection-abort-by-local, stop the API first (so the scan isn't restarting discovery mid-connect)
and re-run to isolate the pure capability question.
"""
import sys, time
import dbus
from dbus.exceptions import DBusException

args = sys.argv[1:]
hold = 10
if "--hold" in args:
    i = args.index("--hold")
    hold = int(args[i + 1]); del args[i:i + 2]
TARGETS = [a.upper().replace("-", ":") for a in args]
if len(TARGETS) < 2:
    print("usage: multi_connect_test.py <MAC> <MAC> [MAC ...] [--hold N]"); sys.exit(2)

bus = dbus.SystemBus()


def ts():
    return time.strftime("%H:%M:%S")


def find_adapter():
    om = dbus.Interface(bus.get_object("org.bluez", "/"), "org.freedesktop.DBus.ObjectManager")
    for path, ifaces in om.GetManagedObjects().items():
        if "org.bluez.Adapter1" in ifaces:
            return path
    raise SystemExit("No Bluetooth adapter found.")


def dev_path(adapter, mac):
    return f"{adapter}/dev_" + mac.replace(":", "_")


def device(adapter, mac):
    return dbus.Interface(bus.get_object("org.bluez", dev_path(adapter, mac)), "org.bluez.Device1")


def props(adapter, mac):
    return dbus.Interface(bus.get_object("org.bluez", dev_path(adapter, mac)), "org.freedesktop.DBus.Properties")


def connected(adapter, mac):
    try:
        return bool(props(adapter, mac).Get("org.bluez.Device1", "Connected"))
    except DBusException:
        return None  # device object not known to BlueZ (never discovered/paired)


adapter = find_adapter()
print(f"[{ts()}] adapter={adapter}  targets={TARGETS}  hold={hold}s")

# Connect each in turn, reporting how long the connect took and whether the EARLIER links survived it.
for mac in TARGETS:
    t0 = time.time()
    try:
        device(adapter, mac).Connect()
        dt = time.time() - t0
        print(f"[{ts()}] CONNECT {mac}  ok in {dt:0.1f}s")
    except DBusException as e:
        dt = time.time() - t0
        print(f"[{ts()}] CONNECT {mac}  FAILED in {dt:0.1f}s: {e.get_dbus_name()} — {e.get_dbus_message()}")
    # Snapshot every target's link state right after each connect — shows if a new link drops an old one.
    print("           state: " + ", ".join(f"{m.split(':')[-1]}={connected(adapter, m)}" for m in TARGETS))

# Hold the links and re-check, so we see whether the controller keeps them all up or quietly drops one.
print(f"[{ts()}] holding {hold}s, re-checking links...")
all_up_seen = False
for _ in range(hold):
    time.sleep(1)
    states = {m: connected(adapter, m) for m in TARGETS}
    if all(states.values()):
        all_up_seen = True
states = {m: connected(adapter, m) for m in TARGETS}
print(f"[{ts()}] final: " + ", ".join(f"{m}={states[m]}" for m in TARGETS))
up = sum(1 for v in states.values() if v)
print(f"[{ts()}] RESULT: {up}/{len(TARGETS)} links up simultaneously"
      + ("  — ALL held at least once during hold" if all_up_seen else ""))

# Clean up: disconnect everything we touched so the lamps return to advertising for the API's scan.
for mac in TARGETS:
    try:
        device(adapter, mac).Disconnect()
    except DBusException:
        pass
print(f"[{ts()}] disconnected all; done.")
