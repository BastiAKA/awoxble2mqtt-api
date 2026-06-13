# AwoxController.MqttBridge

A small standalone service that exposes the AwoxController API to **Home Assistant via MQTT discovery**,
so the lamps/scenes become controllable from any ecosystem HA bridges to (Apple Home, Google, Alexa,
SmartThings — via a Matter bridge add-on).

It is **decoupled**: it talks to the API only over HTTP (REST) + SignalR (`/hubs/lights`). It does not
reference the API project and touches neither the API internals, the frontend, nor the AwoX-app path.
Point it at anything that speaks the same contract.

## What it does

- **Discovery**: on start (and on a slow poll) it `GET`s `/api/devices` + `/api/scenes` and publishes
  retained Home Assistant discovery configs:
  - each lamp → an HA `light` (`schema: json`) with on/off, brightness, `rgb` and `color_temp` (mireds),
    pre-filed into its room via `suggested_area`.
  - each scene → an HA `scene` that applies it (`payload_on = APPLY`).
- **Live state in**: subscribes to the API's SignalR `StateChanged` and mirrors it onto the lamp's MQTT
  state topic (so HA reflects real state, fed by the passive advert scan).
- **Commands out**: subscribes to the HA command topics and translates them to REST calls
  (`/on` `/off` `/color` `/colorTemp` `/brightness` `/colorBrightness`, `/scenes/{id}/apply`). Brightness
  goes on the colour or white channel depending on the active colour mode.

## Topics

| Purpose | Topic |
|---|---|
| Light discovery | `homeassistant/light/<uid>/config` |
| Light state | `awox/light/<uid>/state` |
| Light command | `awox/light/<uid>/set` |
| Light availability | `awox/light/<uid>/availability` |
| Scene discovery | `homeassistant/scene/awox_scene_<id>/config` |
| Scene command | `awox/scene/<id>/set` |

`<uid>` is the lamp MAC without separators. `homeassistant` / `awox` are configurable
(`Mqtt:DiscoveryPrefix` / `Mqtt:BaseTopic`).

## Configure

`appsettings.json` (override per-environment as usual):

```jsonc
"Bridge": {
  "ApiBaseUrl": "http://localhost:5080",   // the AwoxController API
  "ApiKey": "",                            // set if the API has Auth:ApiKey
  "RefreshIntervalSeconds": 60,
  "Mqtt": {
    "Host": "localhost", "Port": 1883,     // your MQTT broker (e.g. Mosquitto / the HA add-on)
    "Username": null, "Password": null,
    "ClientId": "awox-mqtt-bridge",
    "DiscoveryPrefix": "homeassistant",
    "BaseTopic": "awox"
  }
}
```

**Prerequisite:** an MQTT broker reachable at `Mqtt:Host`. (Requires a broker — e.g. Mosquitto or the
Home Assistant Mosquitto add-on.)

## Run / deploy (Pi)

```bash
~/.dotnet/dotnet build src/AwoxController.MqttBridge/AwoxController.MqttBridge.csproj -c Release -f net10.0
ASPNETCORE_ENVIRONMENT=Development \
  ~/.dotnet/dotnet src/AwoxController.MqttBridge/bin/Release/net10.0/AwoxController.MqttBridge.dll
```

Best run as its own `systemd` service alongside `awox-api` (own unit, own logs).
