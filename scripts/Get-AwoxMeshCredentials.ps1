<#
.SYNOPSIS
    Fetches AwoX/Eglo mesh credentials and the device list from the AwoX cloud account.

.DESCRIPTION
    The AwoX/Eglo app stores the mesh name/password in a Parse Server cloud. Logging in with the
    same account email + password you use in the app returns the mesh credentials and the full
    device list (friendly name, MAC, mesh id). These go into appsettings.json under "AwoxBle".

    Field mapping (the cloud reuses OAuth field names):
        MeshName     = Credential.client_id
        MeshPassword = Credential.access_token
        MeshKey      = Credential.refresh_token   (long-term key, for future setMesh use)

    Source of the API details: github.com/fsaris/home-assistant-awox (awox_connect.py).
    This only touches YOUR account and YOUR devices.

.PARAMETER Email
    Your AwoX/Eglo app account email. Prompted if omitted.

.PARAMETER Password
    Your AwoX/Eglo app account password. Prompted securely if omitted.

.PARAMETER OutFile
    Optional path to write the generated "AwoxBle" appsettings snippet to (JSON).

.EXAMPLE
    .\Get-AwoxMeshCredentials.ps1

.EXAMPLE
    .\Get-AwoxMeshCredentials.ps1 -Email me@example.com -OutFile awoxble.json
#>
[CmdletBinding()]
param(
    [string]$Email,
    [string]$Password,
    [string]$OutFile,
    [switch]$DumpRaw
)

$ErrorActionPreference = 'Stop'

if (-not $Email) { $Email = Read-Host 'AwoX/Eglo account email' }
if (-not $Password) {
    $secure = Read-Host 'AwoX/Eglo account password' -AsSecureString
    $Password = [System.Net.NetworkCredential]::new('', $secure).Password
}

$headers = @{
    'x-parse-application-id'  = '55O69FLtoxPt67LLwaHGpHmVWndhZGn9Wty8PLrJ'
    'x-parse-client-key'      = 'PyR3yV65rytEicteNlQHSVNpAGvCByOrsLiEqJtI'
    'x-parse-installation-id' = [guid]::NewGuid().ToString()
    'content-type'            = 'application/json'
}
$bases = @(
    'https://l4hparse-prod.awox.cloud/parse/',      # AwoX/Eglo Connect
    'https://l4hparse-hc-prod.awox.cloud/parse/'    # AwoX HomeControl
)

# --- Login (try both backends) ---------------------------------------------------------------
$login = $null
$base = $null
foreach ($b in $bases) {
    try {
        $login = Invoke-RestMethod -Method Post -Uri ($b + 'login') -Headers $headers `
            -Body (@{ username = $Email; password = $Password; _method = 'GET' } | ConvertTo-Json)
        $base = $b
        Write-Host "Logged in via $b" -ForegroundColor Green
        break
    }
    catch {
        Write-Host "Login failed on $b" -ForegroundColor DarkYellow
    }
}
if (-not $login) {
    throw 'Login failed on both endpoints. Check the email/password (same as the app), or tell which app you use (AwoX HomeControl / AwoX Smart Control / Eglo Connect).'
}

$headers['x-parse-session-token'] = $login.sessionToken
$where = @{
    where   = @{ owner = @{ '__type' = 'Pointer'; className = '_User'; objectId = $login.objectId } }
    _method = 'GET'
} | ConvertTo-Json -Depth 6

# --- Mesh credentials (an account can have MORE THAN ONE mesh) --------------------------------
# Both the "mesh" (older tlmesh bulbs) and "zigbee" (newer Eglo bulbs) services are AwoX/Telink
# mesh networks for our purposes: the login uses client_id as MeshName + access_token as password.
# A bulb's advertised GAP name equals its mesh's client_id, so match the bulb to the right network.
$credRaw = (Invoke-RestMethod -Method Post -Uri ($base + 'classes/Credential') -Headers $headers -Body $where).results
$allCreds = @($credRaw | Where-Object { $_.service -in @('mesh', 'zigbee') } | Sort-Object service)
if ($allCreds.Count -eq 0) { throw 'No mesh/zigbee credentials found on the account.' }

Write-Host ''
Write-Host '=== MESH NETWORKS (each is a separate MeshName/Password) ===' -ForegroundColor Cyan
for ($i = 0; $i -lt $allCreds.Count; $i++) {
    $c = $allCreds[$i]
    Write-Host ("[{0}] service={1}  MeshName={2}  MeshPassword={3}  MeshKey={4}" -f $i, $c.service, $c.client_id, $c.access_token, $c.refresh_token)
}
if ($allCreds.Count -gt 1) {
    Write-Host ("Found {0} networks. A bulb belongs to the network whose MeshName matches its advertised BLE name. If login is rejected (auth error 0x0E), you picked the wrong network." -f $allCreds.Count) -ForegroundColor Yellow
}
# Default to the "zigbee" network (newer Eglo bulbs); override by editing the snippet for tlmesh bulbs.
$creds = @($allCreds | Where-Object { $_.service -eq 'zigbee' })[0]
if (-not $creds) { $creds = $allCreds[0] }

# --- Device list -----------------------------------------------------------------------------
$devices = (Invoke-RestMethod -Method Post -Uri ($base + 'classes/Device') -Headers $headers -Body $where).results

if ($DumpRaw) {
    $credRaw | ConvertTo-Json -Depth 10 | Set-Content -Path 'credentials.raw.json' -Encoding UTF8
    $devices | ConvertTo-Json -Depth 10 | Set-Content -Path 'devices.raw.json' -Encoding UTF8
    Write-Host 'Raw API data written to credentials.raw.json and devices.raw.json' -ForegroundColor Green
}

Write-Host ''
Write-Host '=== DEVICES ===' -ForegroundColor Cyan
$mappedDevices = foreach ($d in $devices) {
    if (-not $d.macAddress -or ($null -eq $d.address)) { continue }
    # Mesh ids are unsigned 16-bit; the cloud returns them signed, so fold negatives back.
    $meshId = [int]$d.address
    if ($meshId -lt 0) { $meshId += 65536 }
    [pscustomobject]@{
        Name   = ($d.displayName).Trim()
        Mac    = $d.macAddress
        MeshId = $meshId
        Model  = if ($d.hardware) { $d.hardware } else { 'AwoX SmartLight' }
    }
}
$mappedDevices | Format-Table Name, Mac, MeshId -AutoSize | Out-String | Write-Host

# --- Generate appsettings "AwoxBle" snippet --------------------------------------------------
$awoxBle = [pscustomobject]@{
    AwoxBle = [pscustomobject]@{
        Enabled      = $true
        MeshName     = $creds.client_id
        MeshPassword = $creds.access_token
        MeshKey      = $creds.refresh_token
        GatewayMac   = ($mappedDevices | Select-Object -First 1 -ExpandProperty Mac)
        Devices      = @($mappedDevices)
    }
}
$json = $awoxBle | ConvertTo-Json -Depth 6

Write-Host '=== appsettings.json snippet (AwoxBle) ===' -ForegroundColor Cyan
Write-Host $json

if ($OutFile) {
    $json | Set-Content -Path $OutFile -Encoding UTF8
    Write-Host ''
    Write-Host "Written to $OutFile" -ForegroundColor Green
}
