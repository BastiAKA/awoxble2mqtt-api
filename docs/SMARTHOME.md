# API → MQTT → Smart Home

How a lamp gets from this API into SmartThings / Apple Home / Google / Alexa, and where each piece sits.

## The path

```
 AwoX lamp ──BLE──> AwoxController.Api ──REST+SignalR──> AwoxController.MqttBridge
                                                                   │
                                                          MQTT (HA discovery)
                                                                   ▼
                                                            MQTT broker (Mosquitto)
                                                                   ▼
                                                            Home Assistant
                                                                   │  Matterbridge add-on
                                                                   ▼  (Matter)
                                              SmartThings · Apple Home · Google · Alexa
```

Each hop is a separate, independently-restartable process. The API never speaks MQTT itself — the
**bridge** is the only component that touches the broker, so the API/frontend/AwoX paths stay untouched.

> **"Zigbee" appears in two unrelated places — don't confuse them:**
> 1. AwoX's lamp *name* "Connect-Z" — these are still **BLE** bulbs (no Zigbee radio, no USB stick).
> 2. A real **Zigbee** radio network — optional, via a USB coordinator + Zigbee2MQTT, a separate input
>    into the API. It is **not** involved in reaching the smart home.
>
> The transport to the smart home is **MQTT** (a message bus) → HA → **Matter**. MQTT ≠ Zigbee; they
> just happen to share the same broker (different topics) if you also run Zigbee2MQTT.

## Who does what

| Component | Runs as | Responsibility |
|-----------|---------|----------------|
| **AwoxController.Api** | `awox-api` (systemd) | BLE control + live advert state; REST + SignalR `/hubs/lights` |
| **AwoxController.MqttBridge** | `awox-mqtt-bridge` (systemd) | publishes lamps/scenes to MQTT (HA discovery); proxies HA commands back to REST |
| **Mosquitto** | `mosquitto` (systemd) | the MQTT broker (message bus) |
| **Home Assistant** | docker (`deploy/smarthome`) | consumes discovery → entities; the user-facing hub |
| **Matterbridge** | docker (`deploy/smarthome`) | exposes HA entities to Matter controllers (commissioned into SmartThings) |

## What the bridge publishes

Retained, so Home Assistant picks them up whenever it (re)starts. `<uid>` = lamp MAC without separators.

| Purpose | Topic | Direction |
|---------|-------|-----------|
| Light discovery | `homeassistant/light/<uid>/config` | bridge → HA |
| Light state | `awox/light/<uid>/state` | bridge → HA |
| Light command | `awox/light/<uid>/set` | HA → bridge |
| Light availability | `awox/light/<uid>/availability` | bridge → HA (`online`/`offline` from reachability) |
| Scene discovery | `homeassistant/scene/awox_scene_<id>/config` | bridge → HA |
| Scene command | `awox/scene/<id>/set` (`APPLY`) | HA → bridge |

Lamps map to HA's `light` with `schema: json` (one topic carries on/off, brightness 0–255, `rgb`,
`color_temp` in mireds). Rooms are passed via `suggested_area`. Scenes map to HA's `scene` (one-shot apply).

## State + command mapping

- **State out** — the bridge subscribes to the API's SignalR `StateChanged` (keyed by MAC) and mirrors it
  onto the lamp's `state` topic, so HA reflects the *real* state fed by the passive advert scan.
- **Command in** — HA publishes a JSON command (`{"state":"ON","brightness":..,"color":{...}}` or
  `color_temp`) to the `set` topic; the bridge translates it to the REST endpoints (`/on` `/off`
  `/color` `/colorTemp` `/brightness` `/colorBrightness`). Brightness is routed to the colour or white
  channel by the active mode.

## Verifying the flow

```bash
# discovery + state the bridge is publishing
mosquitto_sub -h localhost -t 'homeassistant/#' -v      # one config per lamp + scene
mosquitto_sub -h localhost -t 'awox/#' -v               # live state/availability

# simulate an HA command (turns a lamp on via the bridge → API)
mosquitto_pub -h localhost -t 'awox/light/<uid>/set' -m '{"state":"ON","brightness":128}'
```

Config (broker host, API URL, topics, API key): see
[the bridge README](../src/AwoxController.MqttBridge/README.md) and [CONFIGURATION.md](CONFIGURATION.md).
Install steps: [INSTALL.md](INSTALL.md) §6.
