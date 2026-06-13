# AwoX/EGLO "Connect-C" BLE range fix: the metal housing shields the antenna

> Field finding from this project (2026-06-09), shared so others with the same
> lamps don't lose the days we did. Measurements are real, taken on a Raspberry Pi
> with an ASUS USB-BT500 dongle over BlueZ.

## TL;DR

The newer AwoX/EGLO **Connect-C** lamps have a **metal frame/housing around their
BLE controller**, and that metal sits right next to the 2.4 GHz antenna. It
shields and detunes the antenna, costing roughly **15–20 dB** of link budget.
The result is the infamous "Connect-C range is catastrophic" behaviour: the lamp
barely advertises and a controller (hub, not the bundled remote) often can't hold
a GATT connection at all.

**The older Connect-Z lamps don't have this metal around the receiver** and
behave normally.

**Fix:** get the BLE controller out of the metal. We extended the lamp's internal
power lead and relocated the controller board outside the metal enclosure. Range
jumped from "not even detectable" to fully reliable — **independent of where the
hub was**.

This is a hardware/antenna problem, not a firmware or protocol problem. The
Connect-C lamps speak ordinary AwoX/Telink BLE mesh and are perfectly
controllable once the RF link exists.

## Measurements (RSSI at the hub, dBm — higher/less-negative is better)

Same Telink BLE chipset, comparable distances, same hub:

| Lamp type | RSSI seen | Behaviour |
|-----------|-----------|-----------|
| Connect-Z (no metal shield) | −64 … −80 | connects reliably |
| Connect-C (metal around controller) | −82 … −90 | barely advertises, connect aborts |

Before/after on one Connect-C lamp (an EGLO panel):

| State | RSSI | Connect |
|-------|------|---------|
| Controller inside the metal housing | **−90 dBm** | not reachable |
| Controller pulled free of the metal | **−74 dBm** (+16 dB) | connects in ~5 s |
| Controller permanently relocated outside (power lead extended) | **−78 dBm** (+12 dB) | connects in ~4 s |

The ~15–20 dB gap between Connect-C and Connect-Z at the same distances *is* the
shield. Removing the metal recovers almost all of it.

### Why this rules out "just move the hub"

The relocated −78 dBm reading and the 4-second connect above were taken with the
hub **rotated away and mounted high — the exact position where, before the mod,
the lamp couldn't be found at all.** Chasing hub position only buys a few dB and
can't beat a 15–20 dB shield. De-shielding the lamp is the correctly-sized fix.

## Why it also fixes the mesh, not just the direct link

AwoX/Telink BLE mesh relays by flooding: every powered node on the same mesh
rebroadcasts. If **every** Connect-C node is RF-crippled by its own metal housing,
the whole Connect-C mesh is weak — which is why relaying a command from one
Connect-C lamp to another (addressed by mesh id) through a wall fails: both ends
are too quiet.

De-shielding the Connect-C lamps heals the mesh too. Once the nodes transmit
normally, a hub that can reach **one** Connect-C node can drive the rest by mesh
id via relay — so you don't need line-of-sight from the hub to every lamp.

## How to do it

1. **Isolate the lamp at the breaker** before opening anything. These are
   mains-powered fixtures; an angle grinder near mains wiring and a live PCB is no
   joke. Watch for metal swarf/sparks landing on the board.
2. Open the housing and locate the small BLE controller board. The 2.4 GHz
   antenna is usually a PCB chip antenna or a meander trace at one edge of that
   board.
3. Get the antenna **out of the metal**. In order of preference:
   - If the module has a **u.FL/IPEX connector**, fit an external 2.4 GHz antenna
     and route it outside the metal. Best result.
   - Otherwise **relocate the whole controller board** outside the metal enclosure
     (extend its low-voltage leads as needed), so the antenna has air/plastic
     around it.
   - At minimum, give the antenna **≥1 cm of keep-out** from any metal — metal
     within a few mm both detunes and attenuates a chip antenna.
4. Re-measure RSSI before/after to confirm the gain held after reassembly.

## Takeaway

If your AwoX/EGLO Connect-C lamps are "impossible" to reach over BLE from a hub
while the same-brand Connect-Z lamps are fine, it's almost certainly the metal
around the Connect-C controller. It's a fixable antenna-placement problem, not a
protocol or firmware limitation.
