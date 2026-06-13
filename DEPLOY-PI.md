# Running the API on the Raspberry Pi

How to update, build and run **AwoxController.Api** isolated on the Pi, bound to the LAN on port
**5080**. The Pi runs the Linux/BlueZ (`net10.0`) build.

| Thing | Value |
|-------|-------|
| SSH | `ssh basti@192.168.1.53` |
| Repo | `~/NetAwoxLightApi` (git `master`, pulls from GitHub) |
| .NET SDK | `~/.dotnet/dotnet` (10.0.x) |
| Project | `src/AwoxController.Api/AwoxController.Api.csproj` |
| Target framework | `net10.0` (BlueZ backend — **not** the `-windows` TFM) |
| Listen address | `http://0.0.0.0:5080` |
| Log (manual mode) | `~/awox.log` |
| Secrets | `src/AwoxController.Api/appsettings.Development.json` (git-ignored: mesh creds, DB, devices) |

---

## TL;DR — deploy an update

```bash
ssh basti@192.168.1.53
~/NetAwoxLightApi/scripts/pi-api.sh update    # git pull + Release build + restart on :5080
```

That's it. `update` = `git pull --ff-only` → build → stop → start. See [the helper script](scripts/pi-api.sh).

---

## Method A — helper script (no sudo)

The script [`scripts/pi-api.sh`](scripts/pi-api.sh) wraps the whole dance (correct env, LAN bind, safe
stop, detach):

```bash
cd ~/NetAwoxLightApi
chmod +x scripts/pi-api.sh        # once

scripts/pi-api.sh update          # pull + build + restart  (normal deploy)
scripts/pi-api.sh restart         # restart without rebuilding
scripts/pi-api.sh build           # build only
scripts/pi-api.sh status          # is it up on :5080?
scripts/pi-api.sh logs            # tail -f ~/awox.log
scripts/pi-api.sh stop
```

It starts the process **detached** (`setsid nohup … &`) so it keeps running after you log out. Output
goes to `~/awox.log`.

### Or do it by hand

```bash
cd ~/NetAwoxLightApi
git pull --ff-only origin master

# Build the Linux target (ALWAYS rebuild after a pull — see Gotchas).
~/.dotnet/dotnet build src/AwoxController.Api/AwoxController.Api.csproj -c Release -f net10.0

# Stop the old instance by PORT (see Gotchas — do NOT pkill by name).
pid=$(ss -ltnpH | grep ':5080' | grep -oP 'pid=\K[0-9]+' | head -1); [ -n "$pid" ] && kill "$pid"; sleep 3

# Start detached, on the LAN, with the Development config.
cd src/AwoxController.Api
setsid nohup env \
  ASPNETCORE_ENVIRONMENT=Development \
  Kestrel__Endpoints__Http__Url=http://0.0.0.0:5080 \
  ~/.dotnet/dotnet bin/Release/net10.0/AwoxController.Api.dll </dev/null >~/awox.log 2>&1 &

sleep 9; ss -ltn | grep ':5080'        # should show 0.0.0.0:5080
```

---

## Method B — systemd service (recommended, survives reboot)

True isolation + auto-start on boot + auto-restart on crash + logs in journald. Needs `sudo` once.

```bash
cd ~/NetAwoxLightApi
sudo cp scripts/awox-api.service /etc/systemd/system/awox-api.service
sudo systemctl daemon-reload
sudo systemctl enable --now awox-api          # start now + on every boot
```

Unit: [`scripts/awox-api.service`](scripts/awox-api.service). Day-to-day:

```bash
# Deploy an update:
cd ~/NetAwoxLightApi && git pull --ff-only origin master
~/.dotnet/dotnet build src/AwoxController.Api/AwoxController.Api.csproj -c Release -f net10.0
sudo systemctl restart awox-api

sudo systemctl status awox-api
journalctl -u awox-api -f                      # live logs
```

With systemd you do **not** use `pi-api.sh` / `setsid` — systemd owns the process and the port. Pick
one method, not both.

---

## BT dongle watchdog (auto-recovers the wedged adapter, no reboot)

The ASUS BT500 (RTL8761B) periodically wedges in a firmware-download loop and never self-recovers
(see Gotcha 6). A small root timer detects that state and re-enumerates **only** the dongle via its USB
`authorized` node — the proven non-reboot fix (it does **not** touch `dwc_otg`). Independent of the API
process. Needs `sudo` once:

```bash
cd ~/NetAwoxLightApi
chmod +x scripts/awox-bt-watchdog.sh
sudo cp scripts/awox-bt-watchdog.service /etc/systemd/system/
sudo cp scripts/awox-bt-watchdog.timer   /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now awox-bt-watchdog.timer   # checks every 60s (2min after boot)
```

Units: [`scripts/awox-bt-watchdog.service`](scripts/awox-bt-watchdog.service) +
[`scripts/awox-bt-watchdog.timer`](scripts/awox-bt-watchdog.timer), script
[`scripts/awox-bt-watchdog.sh`](scripts/awox-bt-watchdog.sh). Useful commands:

```bash
scripts/awox-bt-watchdog.sh check          # dry-run: report healthy/wedged, take no action (no sudo)
systemctl list-timers awox-bt-watchdog.timer
journalctl -t awox-bt-watchdog -f          # recovery events
sudo systemctl start awox-bt-watchdog.service   # force a check now
```

It only acts on the true wedge (no hci `UP RUNNING` with a non-zero BD address) and confirms the state
is persistent before toggling, so a healthy or merely-busy adapter is never disturbed.

---

## Verify it's reachable

```bash
curl -s http://localhost:5080/api/devices | head        # on the Pi
curl -s http://192.168.1.53:5080/api/devices | head     # from the LAN/PC
```

Swagger UI: <http://192.168.1.53:5080/swagger>.

---

## Gotchas (learned the hard way)

1. **Bind with `Kestrel__Endpoints__Http__Url`, not `ASPNETCORE_URLS`.** `appsettings.json` pins
   Kestrel to `127.0.0.1:5080`; an explicit `Kestrel:Endpoints` section **overrides** `ASPNETCORE_URLS`
   (you'll see *"Overriding address(es) … binding to endpoints defined via IConfiguration"* and it stays
   on localhost → unreachable from the LAN). Override the endpoint key instead, as above.

2. **Never `pkill -f AwoxController.Api.dll` over SSH.** That pattern also matches the shell running your
   command, so it kills your own SSH session (and the start that was riding on it). Stop by the
   port-5080 listener PID instead (the script does this).

3. **Always rebuild after `git pull`.** A stale `bin/Release` binary keeps running the *old* code even
   though the source updated — symptoms look like "my fix didn't take". `pi-api.sh update` always builds.

4. **`ASPNETCORE_ENVIRONMENT=Development` is required.** Without it the app loads `appsettings.json`
   (empty mesh creds / no DB) instead of `appsettings.Development.json`, so BLE + the registry go dark.

5. **Detaching matters.** A bare `dotnet … &` dies when the SSH session closes. Use `setsid nohup …
   </dev/null >log 2>&1 &` (the script does), or systemd.

6. **Bluetooth dongle wedged?** If scans/connects suddenly all fail (RTL8761B stuck in a firmware-
   download loop — `hciconfig` shows `DOWN` / BD address `00:00:00:00:00:00`), the **BT dongle
   watchdog** above auto-recovers it within ~60s by toggling the dongle's USB `authorized` node. To fix
   it by hand: `echo 0 | sudo tee /sys/bus/usb/devices/$DEV/authorized; sleep 2; echo 1 | sudo tee …`
   (find `$DEV` with `lsusb`/sysfs — the BT500 is `0b05:190e`). A **reboot** also works but is the heavy
   hammer. Do **not** unbind/rebind `dwc_otg` — that oopses the kernel.

7. **Build the right TFM.** `-f net10.0` = the BlueZ/Linux backend. The `net10.0-windows…` TFM is for
   local Windows debugging only and its scans return empty on the Pi.
