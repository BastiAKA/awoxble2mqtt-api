#!/usr/bin/env bash
# Manage the AwoxController API on the Raspberry Pi without sudo: update / build / start (detached) /
# stop / restart / status / logs. Encodes the two easy-to-get-wrong bits (LAN bind + safe stop).
#
#   ./scripts/pi-api.sh update     # git pull + build + restart   (the usual "deploy")
#   ./scripts/pi-api.sh restart    # stop + start (no rebuild)
#   ./scripts/pi-api.sh status
#   ./scripts/pi-api.sh logs       # tail -f the log
set -uo pipefail

REPO="$HOME/NetAwoxLightApi"
DOTNET="$HOME/.dotnet/dotnet"
PROJ="$REPO/src/AwoxController.Api/AwoxController.Api.csproj"
APPDIR="$REPO/src/AwoxController.Api"
DLL="bin/Release/net10.0/AwoxController.Api.dll"
PORT=5080
LOG="$HOME/awox.log"

# ASPNETCORE_ENVIRONMENT=Development loads appsettings.Development.json (mesh creds, DB connection,
# device list). Kestrel__Endpoints__Http__Url overrides the 127.0.0.1 binding baked into
# appsettings.json so the API is reachable on the LAN — plain ASPNETCORE_URLS is IGNORED here because
# an explicit Kestrel:Endpoints section in config wins over it.
export ASPNETCORE_ENVIRONMENT=Development
export Kestrel__Endpoints__Http__Url="http://0.0.0.0:${PORT}"

# PID of the process LISTENING on $PORT. We stop by port — never `pkill -f AwoxController.Api.dll`,
# because that pattern also matches this script's own command line and would kill your SSH session.
pid_on_port() { ss -ltnpH 2>/dev/null | grep ":${PORT} " | grep -oP 'pid=\K[0-9]+' | head -1; }

build()  { echo "Building (Release, net10.0)…"; "$DOTNET" build "$PROJ" -c Release -f net10.0 -v m --nologo; }

stop() {
  local pid; pid="$(pid_on_port)"
  if [ -n "$pid" ]; then
    echo "Stopping API (pid $pid on :$PORT)…"; kill "$pid"; sleep 3
    pid="$(pid_on_port)"; [ -n "$pid" ] && { echo "still up — SIGKILL"; kill -9 "$pid"; sleep 1; }
  else
    echo "Nothing listening on :$PORT."
  fi
}

start() {
  [ -n "$(pid_on_port)" ] && { echo "Already running on :$PORT (pid $(pid_on_port))."; return; }
  cd "$APPDIR" || exit 1
  echo "Starting API detached on 0.0.0.0:$PORT (log: $LOG)…"
  # setsid+nohup fully detaches it into its own session so it survives the SSH disconnect; the trailing
  # sleep lets it bind before we report. </dev/null and the redirect free the terminal.
  setsid nohup "$DOTNET" "$DLL" </dev/null >"$LOG" 2>&1 &
  sleep 9
  if [ -n "$(pid_on_port)" ]; then echo "Up. pid $(pid_on_port), listening on 0.0.0.0:$PORT."
  else echo "FAILED to start — last log lines:"; tail -n 20 "$LOG"; exit 1; fi
}

case "${1:-}" in
  update)  cd "$REPO" || exit 1; git pull --ff-only origin master && build && stop && start ;;
  build)   build ;;
  start)   start ;;
  stop)    stop ;;
  restart) stop; start ;;
  status)  pid="$(pid_on_port)"; [ -n "$pid" ] && echo "RUNNING (pid $pid) on :$PORT" || echo "NOT running on :$PORT" ;;
  logs)    tail -n "${2:-40}" -f "$LOG" ;;
  *) echo "usage: $0 {update|build|start|stop|restart|status|logs [N]}"; exit 1 ;;
esac
