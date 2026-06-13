#!/usr/bin/env python3
"""Live passive BLE-advertisement watcher for AwoX lamps (BlueZ D-Bus, no root, no connect).

Proves the lamp broadcasts its full state in the advert: subscribes to BlueZ ManufacturerData
for one lamp MAC and prints decoded mode/brightness/white-temp/hue/sat every time it changes.
Drive the lamp from the AwoX app (on/off, dim, colour, white-temp) and watch the fields track.

BlueZ delivers ManufacturerData with the 2-byte company id stripped (it's the dict key 0x0160),
so all offsets here are 2 less than in the raw manufacturer blob.

Usage: python3 adv_watch.py [MAC] [seconds]
"""
import sys, time
import dbus, dbus.mainloop.glib
from gi.repository import GLib

TARGET = (sys.argv[1] if len(sys.argv) > 1 else "AA:BB:CC:DD:EE:FF").upper()  # pass your lamp MAC as arg
RUN_S = int(sys.argv[2]) if len(sys.argv) > 2 else 300
AWOX_COMPANY = 0x0160
# BlueZ device object paths look like /org/bluez/hci0/dev_A4_C1_38_DA_52_B3
PATH_FRAG = "DEV_" + TARGET.replace(":", "_")

dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
bus = dbus.SystemBus()

last_hex = {}
last_state = {"sig": None, "txt": "(nothing seen yet)", "t": 0}


def find_adapter():
    om = dbus.Interface(bus.get_object("org.bluez", "/"), "org.freedesktop.DBus.ObjectManager")
    for path, ifaces in om.GetManagedObjects().items():
        if "org.bluez.Adapter1" in ifaces:
            return path
    raise RuntimeError("no BlueZ adapter found")


ADAPTER = find_adapter()


def ts():
    return time.strftime("%H:%M:%S")


def decode(v):
    """v = manufacturer data with company id already stripped (BlueZ form)."""
    if len(v) < 18:
        return "POWER=OFF? (base advert only, no state payload)"
    mesh = v[9] | (v[10] << 8)
    mode = v[11]
    bright = v[12]
    wt = v[13] | (v[14] << 8)
    hue, sat = v[15], v[16]
    mname = {0x01: "WHITE", 0x03: "COLOR"}.get(mode, f"0x{mode:02X}")
    # mode byte 0x01/0x03 = ON (white/colour); 0x02 (and others) look like OFF/standby
    power = "ON " if mode in (0x01, 0x03) else "OFF"
    if mode == 0x01:
        detail = f"whiteTemp=0x{wt:04X}"
    elif mode == 0x03:
        detail = f"hue=0x{hue:02X} sat=0x{sat:02X}"
    else:
        detail = f"wt=0x{wt:04X} hue=0x{hue:02X} sat=0x{sat:02X}"
    return (f"POWER={power} mode=0x{mode:02X}({mname}) bright=0x{bright:02X} ({bright*100//255}%) "
            f"{detail}   raw={v.hex().upper()}")


def emit(path, props):
    md = props.get("ManufacturerData")
    if md is None:
        return
    v = bytes(md.get(AWOX_COMPANY, b""))
    if not v:
        return
    txt = decode(v)
    last_state["txt"] = txt
    last_state["t"] = time.time()
    h = v.hex()
    if last_hex.get(path) == h:
        return  # unchanged — BlueZ may repeat; only print real changes
    last_hex[path] = h
    last_state["sig"] = txt
    print(f"[{ts()}] CHANGE  {txt}", flush=True)


def heartbeat():
    # periodic snapshot so steady OFF/ON phases are visible even without a change event
    age = int(time.time() - last_state["t"]) if last_state["t"] else -1
    print(f"[{ts()}] hold    {last_state['txt']}  (last advert {age}s ago)", flush=True)
    return True


def on_props_changed(interface, changed, invalidated, path=None):
    if interface == "org.bluez.Device1" and PATH_FRAG in path.upper():
        emit(path, changed)


def on_iface_added(path, interfaces):
    dev = interfaces.get("org.bluez.Device1")
    if dev and PATH_FRAG in path.upper():
        emit(path, dev)


def main():
    adapter = dbus.Interface(bus.get_object("org.bluez", ADAPTER), "org.bluez.Adapter1")
    # passive LE scan, allow duplicate adverts so we see every state push
    try:
        adapter.SetDiscoveryFilter(dbus.Dictionary(
            {"Transport": dbus.String("le"), "DuplicateData": dbus.Boolean(True)}, signature="sv"))
    except dbus.DBusException as e:
        print("filter warn:", e)
    try:
        adapter.StartDiscovery()
    except dbus.DBusException as e:
        print("StartDiscovery:", e)

    bus.add_signal_receiver(on_props_changed, dbus_interface="org.freedesktop.DBus.Properties",
                            signal_name="PropertiesChanged", arg0="org.bluez.Device1",
                            path_keyword="path")
    bus.add_signal_receiver(on_iface_added, dbus_interface="org.freedesktop.DBus.ObjectManager",
                            signal_name="InterfacesAdded")

    # prime from already-known device (in case it's cached and won't re-add)
    om = dbus.Interface(bus.get_object("org.bluez", "/"), "org.freedesktop.DBus.ObjectManager")
    for path, ifaces in om.GetManagedObjects().items():
        dev = ifaces.get("org.bluez.Device1")
        if dev and PATH_FRAG in path.upper():
            emit(path, dev)

    print(f"[{ts()}] watching {TARGET} for {RUN_S}s — drive the lamp in the app now...", flush=True)
    GLib.timeout_add_seconds(8, heartbeat)

    def stop():
        try:
            adapter.StopDiscovery()
        except dbus.DBusException:
            pass
        loop.quit()

    GLib.timeout_add_seconds(RUN_S, stop)
    loop.run()


loop = GLib.MainLoop()
main()
