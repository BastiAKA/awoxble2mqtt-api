using AwoxController.Api.Security;
using AwoxController.Ble;
using Microsoft.AspNetCore.Mvc;

namespace AwoxController.Api.Controllers;

/// <summary>
/// BLE setup + direct control. Discovery (scan/probe) finds the MAC addresses; the control
/// endpoints drive a bulb directly by MAC, before the friendly-name/device mapping exists.
///
/// Direct control connects to the given MAC as the mesh gateway and sends with a destination mesh
/// id (default 0 = the whole mesh through that bulb). This works even when AwoxBle:Enabled is false;
/// the only config it needs is AwoxBle:MeshName / MeshPassword for the login.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AuthorizeViaApiKey]
public sealed class BleController : ControllerBase
{
    private readonly IAwoxBleScanner _scanner;
    private readonly IAwoxBleConnection _connection;

    public BleController(IAwoxBleScanner scanner, IAwoxBleConnection connection)
    {
        _scanner = scanner;
        _connection = connection;
    }

    /// <summary>
    /// GET /api/ble/scan?seconds=8 — lists BLE devices in range. AwoX-looking devices and ones
    /// advertising the mesh service are returned first.
    /// </summary>
    [HttpGet("scan")]
    public async Task<ActionResult<IReadOnlyList<DiscoveredBleDevice>>> Scan(
        [FromQuery] int seconds = 8, CancellationToken ct = default)
    {
        seconds = Math.Clamp(seconds, 1, 60);
        var devices = await _scanner.ScanAsync(TimeSpan.FromSeconds(seconds), ct);
        return Ok(devices);
    }

    /// <summary>
    /// GET /api/ble/probe/{mac} — connects to one device and reports whether it exposes the AwoX
    /// Telink mesh GATT table (so our protocol can control it). No mesh credentials required.
    /// </summary>
    [HttpGet("probe/{mac}")]
    public async Task<ActionResult<BleProbeResult>> Probe(string mac, CancellationToken ct = default)
    {
        var result = await _scanner.ProbeAsync(mac, ct);
        return Ok(result);
    }

    /// <summary>GET /api/ble/gatt/{mac} — full GATT table dump (diagnostic) as plain text.</summary>
    [HttpGet("gatt/{mac}")]
    public async Task<IActionResult> Gatt(string mac, CancellationToken ct = default)
        => Content(await _scanner.DumpGattAsync(mac, ct), "text/plain");

    // ---- Direct control by MAC (temporary, until device IDs/persistence land) -------------

    /// <summary>POST /api/ble/{mac}/on?destId=0</summary>
    [HttpPost("{mac}/on")]
    public Task<IActionResult> On(string mac, [FromQuery] ushort destId = 0, CancellationToken ct = default)
        => SendAsync(mac, destId, AwoxMeshProtocol.CmdPower, AwoxMeshProtocol.PowerPayload(true), ct);

    /// <summary>POST /api/ble/{mac}/off?destId=0</summary>
    [HttpPost("{mac}/off")]
    public Task<IActionResult> Off(string mac, [FromQuery] ushort destId = 0, CancellationToken ct = default)
        => SendAsync(mac, destId, AwoxMeshProtocol.CmdPower, AwoxMeshProtocol.PowerPayload(false), ct);

    /// <summary>PUT /api/ble/{mac}/brightness — body { "percent": 0-100 } (white channel).</summary>
    [HttpPut("{mac}/brightness")]
    public Task<IActionResult> Brightness(string mac, [FromBody] PercentRequest req, [FromQuery] ushort destId = 0, CancellationToken ct = default)
        => SendAsync(mac, destId, AwoxMeshProtocol.CmdWhiteBrightness, AwoxMeshProtocol.WhiteBrightnessPayload(req.Percent), ct);

    /// <summary>PUT /api/ble/{mac}/color-brightness — body { "percent": 0-100 } (colour channel).</summary>
    [HttpPut("{mac}/color-brightness")]
    public Task<IActionResult> ColorBrightness(string mac, [FromBody] PercentRequest req, [FromQuery] ushort destId = 0, CancellationToken ct = default)
        => SendAsync(mac, destId, AwoxMeshProtocol.CmdColorBrightness, AwoxMeshProtocol.ColorBrightnessPayload(req.Percent), ct);

    /// <summary>PUT /api/ble/{mac}/color — body { "r": 0-255, "g": 0-255, "b": 0-255 }.</summary>
    [HttpPut("{mac}/color")]
    public Task<IActionResult> Color(string mac, [FromBody] ColorRequest req, [FromQuery] ushort destId = 0, CancellationToken ct = default)
        => SendAsync(mac, destId, AwoxMeshProtocol.CmdColor, AwoxMeshProtocol.ColorPayload(req.R, req.G, req.B), ct);

    /// <summary>PUT /api/ble/{mac}/color-temp — body { "mireds": 153-500 }.</summary>
    [HttpPut("{mac}/color-temp")]
    public Task<IActionResult> ColorTemp(string mac, [FromBody] MiredsRequest req, [FromQuery] ushort destId = 0, CancellationToken ct = default)
        => SendAsync(mac, destId, AwoxMeshProtocol.CmdWhiteTemperature, AwoxMeshProtocol.WhiteTemperaturePayload(req.Mireds), ct);

    // ---- Connection lifecycle -----------------------------------------------------------------

    /// <summary>
    /// GET /api/ble/connection — the currently-held BLE session: whether one is open, which gateway
    /// bulb it's on, and how long it's been idle. For a frontend to show/clear the active link.
    /// </summary>
    [HttpGet("connection")]
    public IActionResult Connection()
    {
        var idle = _connection.LastActivityUtc is { } t
            ? (int)Math.Max(0, (DateTimeOffset.UtcNow - t).TotalSeconds)
            : (int?)null;
        return Ok(new
        {
            connected = _connection.IsConnected,
            gatewayMac = _connection.ConnectedGatewayMac,
            idleSeconds = idle,
        });
    }

    /// <summary>
    /// POST /api/ble/disconnect — cleanly drops the held BLE session so the AwoX app can reclaim the
    /// bulb. The next command reconnects automatically. No-op when nothing is connected.
    /// </summary>
    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken ct = default)
    {
        await _connection.DisconnectAsync(ct);
        return Accepted();
    }

    // ---- Diagnostics --------------------------------------------------------------------------

    /// <summary>
    /// POST /api/ble/{mac}/login-test — tries each candidate MeshName/MeshPassword against the bulb
    /// in turn and reports which (if any) the bulb accepts (0x0D) vs rejects (0x0E). Stops at the
    /// first that works. Body: [{ "meshName": "...", "meshPassword": "..." }, ...]. Used to discover
    /// which mesh network a bulb belongs to without restarting the app per attempt.
    /// </summary>
    [HttpPost("{mac}/login-test")]
    public async Task<ActionResult<IReadOnlyList<AwoxLoginTestResult>>> LoginTest(
        string mac, [FromBody] IReadOnlyList<LoginTestCandidate> candidates, CancellationToken ct = default)
    {
        if (candidates is null || candidates.Count == 0)
            return BadRequest("Provide at least one { meshName, meshPassword } candidate.");

        var results = new List<AwoxLoginTestResult>();
        foreach (var c in candidates)
        {
            var result = await _connection.TryLoginAsync(mac, c.MeshName, c.MeshPassword, ct);
            results.Add(result);
            if (result.Ok) break; // found the right credentials — no need to try the rest
        }
        return Ok(results);
    }

    /// <summary>GET /api/ble/{mac}/status — actively reads + decrypts the bulb's status (no notify needed).</summary>
    [HttpGet("{mac}/status")]
    public async Task<IActionResult> Status(string mac, CancellationToken ct = default)
    {
        try
        {
            var decrypted = await _connection.ReadStatusAsync(mac, ct);
            if (decrypted is null)
                return StatusCode(StatusCodes.Status502BadGateway, "Status read but could not be decrypted.");

            return Ok(new
            {
                mac,
                raw = Convert.ToHexString(decrypted),
                meshId = decrypted.Length > 3 ? decrypted[3] : 0,
                mode = decrypted.Length > 12 ? decrypted[12] : 0,
                on = decrypted.Length > 12 && (decrypted[12] % 2) == 1
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    // ---- Newer "Connect-Z" (.ble.zigbee) bulbs: 17-byte AES-ECB command frame ----------------

    /// <summary>POST /api/ble/{mac}/z/on?destId=21198 — Connect-Z power on (destId = bulb mesh id).</summary>
    [HttpPost("{mac}/z/on")]
    public Task<IActionResult> ZOn(string mac, [FromQuery] ushort destId, CancellationToken ct = default)
        => SendZigbeeAsync(mac, destId, AwoxMeshProtocol.ZigbeePowerCommand(true), ct);

    /// <summary>POST /api/ble/{mac}/z/off?destId=21198 — Connect-Z power off.</summary>
    [HttpPost("{mac}/z/off")]
    public Task<IActionResult> ZOff(string mac, [FromQuery] ushort destId, CancellationToken ct = default)
        => SendZigbeeAsync(mac, destId, AwoxMeshProtocol.ZigbeePowerCommand(false), ct);

    /// <summary>PUT /api/ble/{mac}/z/brightness?destId=21198 — body { "percent": 0-100 }.</summary>
    [HttpPut("{mac}/z/brightness")]
    public Task<IActionResult> ZBrightness(string mac, [FromBody] PercentRequest req, [FromQuery] ushort destId, CancellationToken ct = default)
        => SendZigbeeAsync(mac, destId, AwoxMeshProtocol.ZigbeeBrightnessCommand((byte)Math.Clamp(req.Percent * 254 / 100, 1, 254)), ct);

    /// <summary>PUT /api/ble/{mac}/z/color?destId=21198 — body { "r":0-255, "g":..., "b":... }.</summary>
    [HttpPut("{mac}/z/color")]
    public Task<IActionResult> ZColor(string mac, [FromBody] ColorRequest req, [FromQuery] ushort destId, CancellationToken ct = default)
        => SendZigbeeAsync(mac, destId, AwoxMeshProtocol.ZigbeeColorCommand(req.R, req.G, req.B), ct);

    /// <summary>PUT /api/ble/{mac}/z/hue?destId=21198 — body { "hue":0-255, "sat":0-254 } (raw).</summary>
    [HttpPut("{mac}/z/hue")]
    public Task<IActionResult> ZHue(string mac, [FromBody] HueSatRequest req, [FromQuery] ushort destId, CancellationToken ct = default)
        => SendZigbeeAsync(mac, destId, AwoxMeshProtocol.ZigbeeColorCommand((byte)req.Hue, (byte)req.Sat), ct);

    /// <summary>PUT /api/ble/{mac}/z/white?destId=21198 — body { "value":0-255 } white colour temperature (raw; warm/cold direction to be calibrated).</summary>
    [HttpPut("{mac}/z/white")]
    public Task<IActionResult> ZWhite(string mac, [FromBody] ValueRequest req, [FromQuery] ushort destId, CancellationToken ct = default)
        => SendZigbeeAsync(mac, destId, AwoxMeshProtocol.ZigbeeWhiteTempCommand((byte)Math.Clamp(req.Value, 0, 255)), ct);

    private async Task<IActionResult> SendZigbeeAsync(string mac, ushort destId, byte[] command, CancellationToken ct)
    {
        try
        {
            await _connection.SendZigbeeCommandToAsync(mac, destId, command, ct);
            return Accepted();
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    private async Task<IActionResult> SendAsync(string mac, ushort destId, byte command, byte[] payload, CancellationToken ct)
    {
        try
        {
            await _connection.SendCommandToAsync(mac, destId, command, payload, ct);
            return Accepted();
        }
        catch (Exception ex)
        {
            // Most likely: not reachable, login rejected (wrong MeshName/Password), or wrong protocol.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    public sealed record PercentRequest(int Percent);
    public sealed record ColorRequest(byte R, byte G, byte B);
    public sealed record MiredsRequest(int Mireds);
    public sealed record HueSatRequest(int Hue, int Sat);
    public sealed record ValueRequest(int Value);
    public sealed record LoginTestCandidate(string MeshName, string MeshPassword);
}
