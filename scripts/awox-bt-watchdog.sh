#!/usr/bin/env bash
# AwoX BT dongle watchdog — recovers a wedged ASUS USB-BT500 (Realtek RTL8761B) WITHOUT a reboot.
#
# The failure (see DEPLOY-PI.md "BT dongle wedge"): under load / an under-voltage dip, the full-speed
# USB link to the dongle corrupts; the kernel resets the device, which forces a re-download of the 45KB
# firmware over that same marginal link — which then times out:
#   Bluetooth: hciX: RTL: download fw command failed (-110)
#   Bluetooth: hciX: command 0xfc61 tx timeout   →  Resetting usb device  →  repeat forever
# The adapter is left DOWN with BD address 00:00:00:00:00:00 and NEVER self-recovers, because every
# kernel retry re-downloads the firmware over the link that just proved too flaky to carry it.
#
# The fix (proven by hand): toggle the USB `authorized` node, which re-enumerates ONLY the dongle and
# usually gets one clean window for the firmware download to complete. This does NOT touch the dwc_otg
# controller — unbinding/rebinding dwc_otg OOPSes the Pi-3 kernel, so we must never do that.
#
# Runs as root from a systemd timer (awox-bt-watchdog.timer). Pass `check` to dry-run (report only).
# Must run as root: writing /sys/.../authorized is root-only. Reading hciconfig is not.
set -uo pipefail

VENDOR=0b05         # ASUS
PRODUCT=190e        # USB-BT500 (RTL8761B)
TAG=awox-bt-watchdog
TOGGLE_ATTEMPTS=3
SETTLE_SECONDS=6    # time for fw download + hci to come UP after re-enumeration

log() { logger -t "$TAG" -- "$*" 2>/dev/null; echo "$(date '+%H:%M:%S') $*"; }

# Resolve the dongle's USB device dir dynamically (e.g. 1-1.1.3) so a different port still works.
find_dev() {
  local d
  for d in /sys/bus/usb/devices/*/; do
    [ -f "${d}idVendor" ] && [ -f "${d}idProduct" ] || continue
    if [ "$(cat "${d}idVendor")" = "$VENDOR" ] && [ "$(cat "${d}idProduct")" = "$PRODUCT" ]; then
      basename "$d"; return 0
    fi
  done
  return 1
}

# Wedged := the dongle is plugged in, but NO hci is UP RUNNING with a real (non-zero) BD address.
# A healthy or merely-busy adapter (powered, discovering, mid-connect) is still UP RUNNING with a real
# address, so this does not fire during normal operation — only on the true firmware-download wedge.
is_wedged() {
  find_dev >/dev/null || return 1   # no dongle at all → nothing for us to fix
  local out; out=$(hciconfig -a 2>/dev/null)
  echo "$out" | grep -q 'UP RUNNING' \
    && ! echo "$out" | grep -q 'BD Address: 00:00:00:00:00:00' \
    && return 1                     # at least one healthy adapter → not wedged
  return 0
}

toggle() {
  local dev="$1" node="/sys/bus/usb/devices/$1/authorized"
  [ -w "$node" ] || { log "cannot write $node (need root) — aborting"; return 1; }
  echo 0 > "$node"; sleep 2
  echo 1 > "$node"; sleep "$SETTLE_SECONDS"
}

main() {
  local mode="${1:-run}"

  if ! is_wedged; then
    [ "$mode" = check ] && log "check: adapter healthy ($(hciconfig 2>/dev/null | grep -m1 'BD Address' | tr -s ' '))"
    return 0
  fi

  # Confirm it is a persistent wedge, not a momentary blip, before intervening.
  sleep 5
  is_wedged || { log "transient — adapter recovered on its own, no action"; return 0; }

  local dev; dev=$(find_dev) || { log "wedged but dongle not found on USB?! no action"; return 1; }

  if [ "$mode" = check ]; then
    log "check: WEDGED (dev $dev) — would toggle authorized up to $TOGGLE_ATTEMPTS× (dry-run, no action)"
    return 0
  fi

  log "BT adapter wedged (dev $dev) — recovering via authorized toggle"
  local i
  for i in $(seq 1 "$TOGGLE_ATTEMPTS"); do
    toggle "$dev" || return 1
    if ! is_wedged; then
      log "recovered after toggle $i/$TOGGLE_ATTEMPTS ($(hciconfig 2>/dev/null | grep -m1 'BD Address' | tr -s ' '))"
      return 0
    fi
    log "still wedged after toggle $i/$TOGGLE_ATTEMPTS"
  done
  log "STILL wedged after $TOGGLE_ATTEMPTS toggles — a reboot is likely needed"
  return 1
}

main "${1:-run}"
