# Configuration

Two layers:

1. **`appsettings.json`** — startup config (secrets, DB, network). Read once at boot.
2. **`app_settings` table** — runtime-tunable behaviour, changed live via `/api/settings` (no restart).

## Where settings live

| File | Committed? | Holds |
|------|-----------|-------|
| `src/AwoxController.Api/appsettings.json` | yes | non-secret defaults (logging, Kestrel, CORS) |
| `src/AwoxController.Api/appsettings.Development.json` | **no** (git-ignored) | **secrets**: mesh creds, DB connection string, device list, API key |
| `src/AwoxController.Api/appsettings.Development.example.json` | yes | template — copy it to the file above |

The app runs in the **Development** environment on the Pi (the systemd unit sets
`ASPNETCORE_ENVIRONMENT=Development`), so `appsettings.Development.json` is the one that's actually
loaded with your secrets. Keys present there override `appsettings.json`.

```bash
cp src/AwoxController.Api/appsettings.Development.example.json \
   src/AwoxController.Api/appsettings.Development.json
```

## `appsettings.json` reference

### Network — `Kestrel` / `Cors`
```jsonc
"Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:5080" } } }, // LAN-reachable; 127.0.0.1 = local only
"Cors":    { "AllowedOrigins": [] }   // [] = any origin (fine for a LAN appliance); list origins to lock down
```
On the Pi the systemd unit also sets `Kestrel__Endpoints__Http__Url=http://0.0.0.0:5080` because the
`appsettings.json` value pins localhost. HTTPS redirect is Development-only (a Samsung TV won't trust a
self-signed cert), so the Pi stays on plain HTTP.

### Auth — `Auth`
```jsonc
"Auth": { "ApiKey": "" }   // "" = OPEN (no auth). Set a key to require it.
```
When set, send it as `X-Api-Key: <key>`, `Authorization: Bearer <key>`, or `?apiKey=`. The MQTT bridge
has a matching `Bridge:ApiKey`.

### Database — `ConnectionStrings` / `Database`
```jsonc
"ConnectionStrings": { "AwoxDb": "Server=localhost;Port=3306;Database=AWOXHomeDB;User=api_User;Password=StrongPassword123!" },
"Database": {
  "Provider": "mysql",          // "mysql" (MariaDB/MySQL, needs the connection string) or "sqlite" (zero-server file)
  "Bootstrap": "ensureCreated"  // REQUIRED for MariaDB: "migrate" crashes there. ensureCreated = create schema, no migrations
}
```
Must match `database/InitDB.sql` (db name / user / password). A missing/unreachable DB is **non-fatal** —
BLE control + scan still work (direct MAC endpoints), only the device registry/scenes are disabled.
With `ensureCreated`, schema changes are applied by hand — see [INSTALL.md](INSTALL.md) and `database/*.sql`.

### AwoX BLE — `AwoxBle`
```jsonc
"AwoxBle": {
  "Enabled": true,                  // false = backend dormant (scan/probe + direct MAC control still work)
  "MeshName": "your-mesh-name",     // from the AwoX HomeControl app (≤16 bytes UTF-8)
  "MeshPassword": "your-password",  // from the app (≤16 bytes)
  "MeshKey": "",                    // optional; only needed for some crypto paths
  "GatewayMac": "",                 // optional fixed gateway; empty = first reachable device
  "MaxIdleDisconnectSeconds": 60,   // drop the held BLE link after N s idle (0 = hold forever, lowest latency)
  "ConnectTimeoutSeconds": 15,      // wait for "Connected" (BlueZ)
  "ServicesResolvedTimeoutSeconds": 30, // wait for GATT services after connect
  "ConnectMaxAttempts": 2,          // connect+login retries before giving up
  "OperationTimeoutSeconds": 8,     // hard timeout for a single GATT read/write
  "GateAcquireTimeoutSeconds": 90,  // backstop for acquiring the command gate
  "StatusScanEnabled": true,        // passive advert status scan (Linux); on Windows status comes via WinRT
  "Devices": [
    { "Name": "living_room", "Mac": "A4:C1:38:XX:XX:XX", "MeshId": 0, "Model": "AwoX SmartLight" }
  ]
}
```
`Devices` is the fallback when no DB registry is configured. With a DB, the registry is the source of
truth (populate it via cloud import or the API). Find MACs with `GET /api/ble/scan?seconds=8` and confirm
with `GET /api/ble/probe/{mac}` (`speaksAwoxMesh: true` = controllable).

### Zigbee2MQTT (optional, for *real* Zigbee devices) — `Zigbee2Mqtt`
```jsonc
"Zigbee2Mqtt": { "Enabled": false, "Host": "localhost", "Port": 1883, "BaseTopic": "zigbee2mqtt" }
```
Unrelated to the AwoX "Connect-Z" lamps (those are BLE). Only needed if you attach a Zigbee USB
coordinator + run Zigbee2MQTT. `BaseTopic` must match Z2M's `configuration.yaml`.

## Runtime-tunable — the `app_settings` table

> **These are the timing / delay knobs.** If commands feel slow or laggy, or the live state lags, this
> table is where you tune it — `ble.connect_settle_ms` (pause after a cold connect), `ble.poll_interval_seconds`
> / `ble.advert_fast_poll_ms` (how fast live state refreshes), `ble.relay_verify_timeout_ms` (confirmation
> wait), `ble.idle_disconnect_seconds` (hold-the-link-vs-release latency trade-off). All change live, no restart.

Single key + value rows, read on each lookup (no restart). Inspect/change via the API:

```bash
curl http://localhost:5080/api/settings                       # list all
curl -X PUT http://localhost:5080/api/settings/ble.poll_interval_seconds \
     -H "Content-Type: application/json" -d '{"value":"10"}'
```

Defaults are seeded automatically by the API on first start (and pre-seeded by
`database/SeedSettings.sql`, which `CreateDBEnv.ps1` runs):

| Key | Default | What it does |
|-----|---------|--------------|
| `ble.poll_interval_seconds` | `5` | advert status scan cadence when idle |
| `ble.advert_fast_poll_ms` | `400` | scan cadence while a relay-verify is awaiting a confirming advert |
| `ble.connect_settle_ms` | `600` | settle pause after a cold GATT connect before the first write |
| `ble.idle_disconnect_seconds` | `12` | drop the held link after this idle (overrides the static option at runtime) |
| `ble.offline_after_seconds` | `600` | how stale a last-seen may be before a lamp counts as offline |
| `ble.relay_verify_enabled` | `true` | master switch for relay-through-a-sibling + advert confirmation |
| `ble.relay_verify_timeout_ms` | `5000` | how long to wait for the target's confirming advert |
| `ble.relay_reachable_ttl_seconds` | `30` | how long a learned "reachable (host→target)" verdict is trusted |
| `ble.relay_unreachable_ttl_seconds` | `120` | how long a learned "unreachable" verdict is trusted before re-probing |
| `ble.max_connections` | `2` | max held BLE sessions, one per mesh — lets commands for different meshes run concurrently instead of reconnecting on each mesh switch. `1` = legacy single-session behaviour |

## MQTT bridge config

The bridge service (HA discovery) is configured separately — see
[src/AwoxController.MqttBridge/README.md](../src/AwoxController.MqttBridge/README.md) (`Bridge` section:
`ApiBaseUrl`, `ApiKey`, `Mqtt` host/port/topics).
