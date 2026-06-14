# Roadmap & development journey

How this project got from "control a lamp" to a local-first AwoX → Matter bridge — and what's next.

## How we got here

The goal: control AwoX/EGLO lamps locally (no cloud, no phone app), from a Raspberry Pi, ultimately
reachable from a Samsung SmartThings hub.

1. **Zigbee2MQTT skeleton.** First cut assumed the lamps were ordinary Zigbee devices, controlled via
   Zigbee2MQTT over MQTT. Turned out the AwoX bulbs (incl. the "Connect-Z" ones) speak a **BLE mesh**,
   not Zigbee — so this became an optional side path for *real* Zigbee devices.
2. **AwoX BLE mesh, reverse-engineered.** The bulbs use the AwoX/Telink mesh: connect to one reachable
   bulb (the gateway), log in with the mesh name/password, derive an AES session key, address bulbs by
   mesh id. Implemented directly in .NET — no Python sidecar.
3. **Control backend.** Power/colour/white-temp/brightness for "Connect-C" (classic tlmesh opcodes) and
   "Connect-Z" (AES-ECB command frames). Same login either way.
4. **Cloud import (optional).** A one-time pull of mesh credentials + the device list from the AwoX app
   account, so MACs/mesh-ids don't have to be transcribed by hand.
5. **The key insight — passive advert status.** AwoX bulbs broadcast their full state, unencrypted, in
   their BLE advertisements. Reading it needs **no connection, no login** — a passive scan that never
   steals the link from the app/hub. This became the live-state source.
6. **Registry + runtime config.** EF Core device/scene registry (MariaDB; `ensureCreated` because EF
   migrations crash on MariaDB) and a DB-backed `app_settings` table tunable live via `/api/settings`.
7. **Live push.** SignalR `/hubs/lights` fans every state change out to clients, keyed by MAC.
8. **Relay-verify.** A command to an out-of-range lamp is relayed through a reachable mesh sibling, then
   **confirmed against the target's own advertisement** — and reachable routes are learned. The clock for
   the confirmation starts *after* the write (a cold connect must not eat the window). Unconfirmed relays
   fall back to a direct connect.
9. **Stability.** Fixed an OOM (BlueZ proxies were `IDisposable` but never disposed, leaking D-Bus
   watchers on every poll); made the cold-connect settle tunable; tamed dongle/adapter churn.
10. **Scenes.** Named lamp+state sets, applied **grouped by mesh** (one connect per mesh, the rest
    relayed) so a mixed-mesh scene doesn't thrash the radio.
11. **Windows parity.** A WinRT advert-status path so the whole stack (incl. relay-verify) runs on
    Windows too, not just BlueZ/Linux.
12. **Smart-home bridge.** Rather than hand-rolling Matter, a standalone MQTT bridge publishes everything
    in Home Assistant's discovery format; HA + a Matterbridge add-on expose it to SmartThings/Apple/
    Google/Alexa. See [SMARTHOME.md](SMARTHOME.md).
13. **Dimmer/mode fixes.** White-dim no longer flips Connect-C to colour; an off lamp is powered on
    before a colour command; Connect-Z/Connect-C brightness reporting no longer jumps on a mode switch.
14. **Multi-mesh connection pool.** The backend holds one GATT session **per mesh** (up to
    `ble.max_connections`, default 2) instead of a single session that reconnected on every mesh switch,
    and the command queue drains different meshes **concurrently** (serial within a mesh, which already
    fans out via relay/broadcast). `ble.max_connections=1` restores the old single-session behaviour.
15. **Docs + ops.** This documentation, per-table DB scripts + `CreateDBEnv.ps1`, and a
    build-on-dev-box / copy-DLLs deploy (`scripts/deploy-from-windows.ps1`) so the 1 GB Pi never compiles.

## What's next

- **Frontend.** A web UI (ASP.NET Core BFF + Angular) exists; publish it separately once a few bugs are
  ironed out.
- **Real Zigbee devices.** The Zigbee2MQTT path is wired but dormant (`Zigbee2Mqtt:Enabled=false`) —
  enable + test with a coordinator stick.
- **Colour advert-confirm.** Occasional "no advert confirm" on the colour channel — tighten the
  HSV→RGB matching tolerance / timing.
- **Connect-C white-temp calibration.** The mireds→byte scale is a best-effort linear map; pin it with a
  cold/warm hardware sweep.
- **Tuning review.** Revisit the Linux "slow-downs" (settle/timeout/poll values in `app_settings`) now
  that background relay-verify is in place.
- **Host sizing.** HA + Matterbridge are heavy on a 1 GB Pi; consider moving them to a bigger-RAM host,
  leaving API + broker + bridge on this one.
- **Public release.** Apache-2.0; published as a clean snapshot (no history — early commits held
  secrets).

See also: [README](../README.md) · [INSTALL](INSTALL.md) · [CONFIGURATION](CONFIGURATION.md) ·
[SMARTHOME](SMARTHOME.md) · [DEPLOY-PI](../DEPLOY-PI.md).
