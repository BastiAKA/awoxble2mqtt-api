# Installation

End-to-end setup. For day-to-day Pi operation (systemd units, restart mechanics, gotchas) see
[DEPLOY-PI.md](../DEPLOY-PI.md); for every setting see [CONFIGURATION.md](CONFIGURATION.md).

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` → 10.x).
- **MariaDB or MySQL** (optional but recommended — without it the device registry/scenes are off, but
  BLE scan + direct MAC control still work). SQLite is also supported (`Database:Provider = sqlite`).
- A **Bluetooth adapter**: the Pi's built-in or a USB dongle (Linux/BlueZ); on Windows the built-in radio.
- For the smart-home bridge: an **MQTT broker** (Mosquitto), **Home Assistant**, and the **Matterbridge**
  add-on. All optional — the API + MQTT bridge work without them.

## 1. Get the code

```bash
git clone <repo-url> NetAwoxLightApi && cd NetAwoxLightApi
dotnet restore
```

## 2. Database

Create the database, application user **and all tables** in one go (idempotent, correct FK order):
```powershell
# edit database/InitDB.sql first if you want a different db name / user / password
./database/CreateDBEnv.ps1 -Password <rootPassword>                 # local DB
./database/CreateDBEnv.ps1 -MySqlHost 192.168.1.53 -Password <pw>   # remote DB (the Pi)
```
This runs `InitDB.sql` (DB + user), each `database/tables/NN_*.sql` (meshes → lamps → app_settings
→ scenes → scene_items), then `SeedSettings.sql` (the default runtime tunables). Requires the `mysql` client on PATH. To do it by hand, run those files in the
same order. **MariaDB note:** the app uses `Database:Bootstrap = ensureCreated` (EF migrations crash on
MariaDB); with the tables already created it's a no-op. See CONFIGURATION.md.

## 3. Configuration & secrets

```bash
cp src/AwoxController.Api/appsettings.Development.example.json \
   src/AwoxController.Api/appsettings.Development.json
```
Edit `appsettings.Development.json`:
- `AwoxBle.MeshName` / `MeshPassword` — your mesh credentials (from the AwoX HomeControl app).
- `ConnectionStrings.AwoxDb` — match `database/InitDB.sql`.
- add `"Database": { "Bootstrap": "ensureCreated" }`.
- `AwoxBle.Devices` — lamp MACs (or leave empty and use cloud import / the DB registry).

Full reference: [CONFIGURATION.md](CONFIGURATION.md).

## 4. Run

```bash
# Linux / Raspberry Pi (BlueZ)
dotnet run --project src/AwoxController.Api -f net10.0
# Windows (WinRT, built-in Bluetooth)
dotnet run --project src/AwoxController.Api -f net10.0-windows10.0.19041.0
```
API: `http://localhost:5080` · Swagger: `/swagger`. From the LAN: `http://<host>:5080`.

> **Latency / delays:** if commands feel slow or live state lags, the timing knobs (connect-settle, poll
> cadence, relay-verify timeout, idle-disconnect) are runtime-tunable in the `app_settings` table — no
> rebuild, no restart. See the [tuning table in CONFIGURATION.md](CONFIGURATION.md#runtime-tunable--the-app_settings-table).

### Find your bulbs
```bash
curl "http://localhost:5080/api/ble/scan?seconds=8"      # AwoX-looking devices first
curl "http://localhost:5080/api/ble/probe/A4:C1:38:..."  # speaksAwoxMesh:true = controllable
```
AwoX/EGLO bulbs (Telink, OUI `A4:C1:38`) don't advertise their mesh service until connected — probe to
confirm. Put confirmed MACs into `AwoxBle.Devices` or the DB registry.

### Import lamps from your AwoX account (recommended)

Instead of transcribing MACs and mesh ids by hand, import them in one call from your AwoX/EGLO app
account. The API logs into the AwoX/Eglo **Parse cloud**, fetches your **mesh credentials + the full
device list**, and writes them into the registry (needs the DB from step 2):

```bash
curl -X POST http://localhost:5080/api/devices/import/cloud \
     -H "Content-Type: application/json" \
     -d '{"email":"you@example.com","password":"your-awox-app-password"}'
# → { "meshes": N, "added": N, "updated": N, "total": N }
```

This is the **same account** you use in the AwoX HomeControl / EGLO Connect app. The credentials are
used only for that one request (to fetch the mesh name/password + devices) — they are **not stored**.
After import, the mesh credentials live in the DB, so you can leave `AwoxBle.MeshName/MeshPassword`
empty. It's a one-time step; re-run it when you add lamps in the app.

## 5. Run as a service (Pi)

Install the systemd unit so it survives reboots + auto-restarts. Full details + the BT-dongle watchdog:
[DEPLOY-PI.md](../DEPLOY-PI.md).
```bash
sudo cp scripts/awox-api.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now awox-api
```

## 6. Smart-home bridge (optional) → Home Assistant → SmartThings/Apple/Google/Alexa

### a) MQTT broker (Mosquitto)
```bash
sudo apt install -y mosquitto mosquitto-clients
sudo cp scripts/mosquitto-awox.conf /etc/mosquitto/conf.d/awox.conf
sudo systemctl enable --now mosquitto
```

### b) The MQTT bridge service
Publishes all lamps + scenes to MQTT in HA-discovery form. Config: the `Bridge` section in
[its appsettings](../src/AwoxController.MqttBridge/appsettings.json) (defaults: API `localhost:5080`,
broker `localhost:1883`).
```bash
sudo cp scripts/awox-mqtt-bridge.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now awox-mqtt-bridge
# verify discovery is published:
mosquitto_sub -t 'homeassistant/#' -v
```

### c) Home Assistant + Matterbridge
```bash
sudo docker compose -f deploy/smarthome/docker-compose.yml up -d
```
- **HA** (`http://<pi>:8123`) — onboard, add the MQTT integration (`localhost:1883`); your lamps/scenes
  appear automatically. Create a long-lived token (Profile → Security).
- **Matterbridge** (`http://<pi>:8283`) — add the `matterbridge-hass` plugin, paste the HA token, mode
  **bridge**, then scan the shown QR/pairing code in the **SmartThings app** (Add device → Matter).

> ⚠️ **Resource note:** Home Assistant + Matterbridge are heavy on a 1 GB Pi — running them next to a
> `dotnet build` will exhaust RAM and wedge it. Build elsewhere (below), and consider a bigger-RAM host
> for HA if memory stays tight (`free -h`). The API + bridge themselves are lightweight.

## 7. Deploying updates without building on the Pi

`.NET framework-dependent output is architecture-independent IL`, so build on a dev box and copy the
DLLs — the Pi never compiles:
```powershell
.\scripts\deploy-from-windows.ps1            # publishes the API, scp's it, restarts
.\scripts\deploy-from-windows.ps1 -Bridge    # also the MQTT bridge
```

## Windows (development)

Install the .NET 10 SDK, then run the Windows TFM (`-f net10.0-windows10.0.19041.0`). The WinRT backend
uses the machine's built-in Bluetooth (a Zigbee USB stick is **not** needed for AwoX lamps — they're BLE).
Make sure Bluetooth is **on** (the advert watcher needs the radio).
