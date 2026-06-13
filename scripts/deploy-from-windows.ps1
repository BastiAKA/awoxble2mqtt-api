# Build the AwoxController API (and optionally the MQTT bridge) on THIS Windows box and deploy the
# portable framework-dependent DLLs to the Pi — nothing is compiled on the Pi (a Pi-side Release build
# alongside Home Assistant exhausts its RAM). .NET assemblies are architecture-independent IL, so the
# linux net10.0 output runs as-is on the Pi's existing runtime.
#
#   .\scripts\deploy-from-windows.ps1            # API only
#   .\scripts\deploy-from-windows.ps1 -Bridge    # API + MQTT bridge
#
# Restart is a kill -9 of the service's main process → its systemd on-failure autostart loads the new
# binary (a clean SIGTERM would exit 0 and NOT restart — see the pi-deploy notes).

param(
    [switch]$Bridge,
    [string]$PiHost = "basti@192.168.1.53",
    [string]$Key = "$HOME/.ssh/awox_pi"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

function Deploy($proj, $tfmArgs, $remoteDir, $unit) {
    $out = "$repo/publish/$unit"
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    & dotnet publish "$repo/src/$proj" -c Release @tfmArgs -o $out --nologo
    if ($LASTEXITCODE) { throw "publish $proj failed" }
    & scp -i $Key -r "$out/*" "${PiHost}:~/NetAwoxLightApi/src/$($proj -replace '/.*','')/bin/Release/net10.0/"
    if ($LASTEXITCODE) { throw "scp $unit failed" }
    & ssh -i $Key $PiHost "kill -9 `$(systemctl show -p MainPID --value $unit)"
    Write-Host "$unit deployed + restarted."
}

Deploy "AwoxController.Api/AwoxController.Api.csproj" @("-f","net10.0") "AwoxController.Api" "awox-api"
if ($Bridge) {
    Deploy "AwoxController.MqttBridge/AwoxController.MqttBridge.csproj" @() "AwoxController.MqttBridge" "awox-mqtt-bridge"
}
