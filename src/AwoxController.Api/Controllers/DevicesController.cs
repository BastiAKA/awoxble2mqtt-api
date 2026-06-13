using System.Text.Json;
using AwoxController.Api.Security;
using AwoxController.Api.Services;
using AwoxController.Ble;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using AwoxController.Data;
using Microsoft.AspNetCore.Mvc;

namespace AwoxController.Api.Controllers;

/// <summary>
/// Device registry: list/add/rename/remove bulbs and bulk-import them from the AwoX cloud.
/// Backed by MySQL (<see cref="IDeviceStore"/>). Only available when a DB connection is configured.
/// </summary>
[ApiController]
[Route("api/devices")]
[AuthorizeViaApiKey]
public sealed class DevicesController : ControllerBase
{
    private readonly IDeviceStore _store;
    private readonly AwoxCloudClient _cloud;
    private readonly IAwoxBleConnection _ble;
    private readonly IBleCommandQueue _queue;
    private readonly ILightStateNotifier _notifier;
    private readonly ILightStateCache _stateCache;
    private readonly IMeshGatewayResolver _gateways;
    private readonly ILogger<DevicesController> _log;

    public DevicesController(IDeviceStore store, AwoxCloudClient cloud, IAwoxBleConnection ble,
        IBleCommandQueue queue, ILightStateNotifier notifier, ILightStateCache stateCache,
        IMeshGatewayResolver gateways, ILogger<DevicesController> log)
    {
        _store = store;
        _cloud = cloud;
        _ble = ble;
        _queue = queue;
        _notifier = notifier;
        _stateCache = stateCache;
        _gateways = gateways;
        _log = log;
    }

    /// <summary>GET /api/devices — all bulbs.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<LampDto>> List(CancellationToken ct)
    {
        var window = _gateways.OnlineWindow;
        return (await _store.GetLampsAsync(ct)).Select(l => LampDto.From(l, _stateCache, window)).ToList();
    }

    /// <summary>GET /api/devices/{id}</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<LampDto>> Get(int id, CancellationToken ct)
    {
        var lamp = await _store.GetLampByIdAsync(id, ct);
        return lamp is null ? NotFound() : LampDto.From(lamp, _stateCache, _gateways.OnlineWindow);
    }

    /// <summary>GET /api/devices/meshes — the mesh networks (credentials redacted).</summary>
    [HttpGet("meshes")]
    public async Task<IReadOnlyList<MeshDto>> Meshes(CancellationToken ct)
        => (await _store.GetMeshesAsync(ct)).Select(MeshDto.From).ToList();

    /// <summary>POST /api/devices — add a bulb manually (e.g. after a scan + blink).</summary>
    [HttpPost]
    public async Task<ActionResult<LampDto>> Add([FromBody] AddLampRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Mac))
            return BadRequest("Name and Mac are required.");
        if (await _store.GetLampByMacAsync(req.Mac.ToLowerInvariant(), ct) is not null)
            return Conflict($"A device with MAC {req.Mac} already exists.");

        var lamp = new LampDevice
        {
            Name = req.Name.Trim(),
            Mac = req.Mac.ToLowerInvariant(),
            MeshId = req.MeshId,
            Protocol = req.Protocol,
            Model = req.Model ?? "AwoX SmartLight",
            DeviceType = string.IsNullOrWhiteSpace(req.DeviceType) ? null : req.DeviceType.Trim(),
            Room = string.IsNullOrWhiteSpace(req.Room) ? null : req.Room.Trim(),
            SeparateWhiteColor = req.SeparateWhiteColor ?? false,
            MeshNetworkId = req.MeshNetworkId is > 0 ? req.MeshNetworkId : null,
        };
        await _store.AddLampAsync(lamp, ct);
        return CreatedAtAction(nameof(Get), new { id = lamp.Id }, LampDto.From(lamp, _stateCache, _gateways.OnlineWindow));
    }

    /// <summary>PUT /api/devices/{id} — rename / set room / enable-disable / fix mesh id.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<LampDto>> Update(int id, [FromBody] UpdateLampRequest req, CancellationToken ct)
    {
        var lamp = await _store.GetLampByIdAsync(id, ct);
        if (lamp is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.Name)) lamp.Name = req.Name.Trim();
        // DeviceType/Room: omit (null) to leave unchanged; send "" to clear back to null.
        if (req.DeviceType is not null) lamp.DeviceType = string.IsNullOrWhiteSpace(req.DeviceType) ? null : req.DeviceType.Trim();
        if (req.Room is not null) lamp.Room = string.IsNullOrWhiteSpace(req.Room) ? null : req.Room.Trim();
        if (req.MeshId is not null) lamp.MeshId = req.MeshId.Value;
        if (req.Enabled is not null) lamp.Enabled = req.Enabled.Value;
        if (req.SeparateWhiteColor is not null) lamp.SeparateWhiteColor = req.SeparateWhiteColor.Value;
        await _store.UpdateLampAsync(lamp, ct);
        return LampDto.From(lamp, _stateCache, _gateways.OnlineWindow);
    }

    /// <summary>DELETE /api/devices/{id}</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
        => await _store.RemoveLampAsync(id, ct) ? NoContent() : NotFound();

    /// <summary>
    /// POST /api/devices/import/cloud — Variant 1: log into the AwoX cloud with the app account and
    /// import all meshes + devices. Existing lamps (matched by MAC) keep their name/room.
    /// </summary>
    [HttpPost("import/cloud")]
    public async Task<ActionResult<ImportResultDto>> ImportCloud([FromBody] CloudImportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest("Email and Password are required (your AwoX/Eglo app account).");

        try
        {
            var fetched = await _cloud.FetchAsync(req.Email, req.Password, ct);
            var (meshes, added, updated) = await _store.ImportAsync(fetched.Meshes, fetched.Lamps, ct);
            return new ImportResultDto(meshes, added, updated, fetched.Lamps.Count);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    // ---- Control by registry entry (resolves mesh creds + mesh id + protocol from the DB) ------

    // All control endpoints accept an optional ?via=<gateway> — a reachable mesh node (lamp id/name/MAC)
    // to connect to as the gateway, relaying the command to the target by its mesh id. Use it when the
    // target bulb itself is out of BLE range of the Pi but another node on the same mesh is reachable.

    /// <summary>POST /api/devices/{key}/on — key = id, name, or MAC. ?via=&lt;gateway&gt; to relay.</summary>
    [HttpPost("{key}/on")]
    public Task<IActionResult> CtrlOn(string key, CancellationToken ct, [FromQuery] string? via = null) => Drive(key, ct, on: true, gateway: via);

    /// <summary>POST /api/devices/{key}/off — ?via=&lt;gateway&gt; to relay.</summary>
    [HttpPost("{key}/off")]
    public Task<IActionResult> CtrlOff(string key, CancellationToken ct, [FromQuery] string? via = null) => Drive(key, ct, on: false, gateway: via);

    /// <summary>PUT /api/devices/{key}/color — body { "r":0-255, "g":..., "b":... }. ?via= to relay.</summary>
    [HttpPut("{key}/color")]
    public Task<IActionResult> CtrlColor(string key, [FromBody] ColorRequest req, CancellationToken ct, [FromQuery] string? via = null)
        => Drive(key, ct, color: req, gateway: via);

    /// <summary>PUT /api/devices/{key}/brightness — body { "percent":0-100 } (white channel). ?via= to relay.</summary>
    [HttpPut("{key}/brightness")]
    public Task<IActionResult> CtrlBrightness(string key, [FromBody] PercentRequest req, CancellationToken ct, [FromQuery] string? via = null)
        => Drive(key, ct, brightnessPercent: Math.Clamp(req.Percent, 0, 100), gateway: via);

    /// <summary>PUT /api/devices/{key}/colorBrightness — body { "percent":0-100 } (colour channel). ?via= to relay.
    /// "color-brightness" kept as an alias; "colorBrightness" is the contract spelling.</summary>
    [HttpPut("{key}/color-brightness")]
    [HttpPut("{key}/colorBrightness")]
    public Task<IActionResult> CtrlColorBrightness(string key, [FromBody] PercentRequest req, CancellationToken ct, [FromQuery] string? via = null)
        => Drive(key, ct, colorBrightnessPercent: Math.Clamp(req.Percent, 0, 100), gateway: via);

    /// <summary>PUT /api/devices/{key}/colorTemp — body { "mireds":153-500 } (153 cold .. 500 warm). ?via= to relay.
    /// "color-temp" kept as an alias; "colorTemp" is the contract spelling.</summary>
    [HttpPut("{key}/color-temp")]
    [HttpPut("{key}/colorTemp")]
    public Task<IActionResult> CtrlColorTemp(string key, [FromBody] MiredsRequest req, CancellationToken ct, [FromQuery] string? via = null)
        => Drive(key, ct, mireds: Math.Clamp(req.Mireds, 153, 500), gateway: via);

    /// <summary>
    /// POST /api/devices/{key}/disconnect — drops the held BLE session if this lamp is the bulb that
    /// currently holds it (the active gateway), so the AwoX app can reclaim it. Only ever one session
    /// is held at a time; if a different bulb holds it, this is a no-op (reported in the response).
    /// </summary>
    [HttpPost("{key}/disconnect")]
    public async Task<IActionResult> CtrlDisconnect(string key, CancellationToken ct)
    {
        var lamp = await ResolveLampAsync(key, ct);
        if (lamp is null) return NotFound($"No lamp matching '{key}'.");

        var gw = _ble.ConnectedGatewayMac;
        if (gw is null)
            return Ok(new { disconnected = false, reason = "No BLE session is currently held." });
        if (!string.Equals(gw, lamp.Mac, StringComparison.OrdinalIgnoreCase))
            return Ok(new { disconnected = false, reason = $"The held session is on {gw}, not this lamp." });

        await _ble.DisconnectAsync(ct);
        return Ok(new { disconnected = true, gatewayMac = gw });
    }

    private async Task<IActionResult> Drive(string key, CancellationToken ct,
        bool? on = null, ColorRequest? color = null, int? brightnessPercent = null,
        int? colorBrightnessPercent = null, int? mireds = null, string? gateway = null)
    {
        var lamp = await ResolveLampAsync(key, ct);
        if (lamp is null) return NotFound($"No lamp matching '{key}'.");
        if (lamp.Mesh is null) return BadRequest($"Lamp '{lamp.Name}' has no mesh assigned (run cloud import or set its mesh).");

        // Gateway = the bulb to actually connect to. Default: a REACHABLE node on the lamp's mesh (the
        // target itself when it's online, else a reachable sibling), so a command never stalls on a long
        // failed connect to an offline target. A held same-mesh session is reused regardless (mesh relay,
        // below). With an explicit ?via=<gateway> we instead pin control to that specific node and relay
        // to the target by its mesh id (for a target out of direct BLE range). gateway may be id/name/MAC.
        var forceVia = !string.IsNullOrWhiteSpace(gateway);
        string gw;
        if (forceVia)
        {
            var gwLamp = await ResolveLampAsync(gateway!, ct);
            gw = gwLamp?.Mac ?? gateway!.ToLowerInvariant();
        }
        else
        {
            gw = await _gateways.ResolveGatewayAsync(lamp, ct);
        }
        var (mn, mp) = (lamp.Mesh.MeshName, lamp.Mesh.MeshPassword);
        var dest = (ushort)lamp.MeshId;
        var zigbee = lamp.Protocol == LightProtocol.Zigbee;

        // Mode-preserving dim: a single-channel lamp's only brightness control is /brightness, which on
        // tlmesh maps to the WHITE channel (opcode 0xF1) — and that command flips a colour-mode lamp
        // (notably Connect-C) to white. So when such a lamp is currently showing colour, redirect the
        // dim to the colour-brightness channel (0xF2), which keeps the colour. Separate-channel lamps
        // (Rovito Z) drive white vs colour brightness explicitly from the UI, so they're left as-is.
        if (brightnessPercent is not null && !zigbee && !lamp.SeparateWhiteColor)
        {
            var colorMode = IsCurrentlyColorMode(lamp);
            _log.LogInformation("Dim {Lamp}: {Pct}% → {Channel} channel (colourMode={Mode})",
                lamp.Name, brightnessPercent, colorMode ? "COLOUR(0xF2)" : "WHITE(0xF1)", colorMode);
            if (colorMode)
            {
                colorBrightnessPercent = brightnessPercent;
                brightnessPercent = null;
            }
        }

        // tlmesh (Connect C + older bulbs): standard 20-byte command frame, opcodes D0/E2/F2/F1/F0.
        // zigbee (Connect-Z / EGLO-ZM): 17-byte AES-ECB frame, separate helpers. Same login either way.
        // The payload is built here (once); the queue worker supplies its own token at send time — never
        // the request ct, which is cancelled the moment we return 202.
        // The GATT write takes the gateway MAC as a PARAMETER so the relay coordinator can route it
        // either through an already-held same-mesh node (relay) or directly to the target.
        Func<IAwoxBleConnection, string, CancellationToken, Task> Tlmesh(byte cmd, byte[] data)
            => (conn, g, t) => conn.SendCommandToAsync(g, mn, mp, dest, cmd, data, t);
        Func<IAwoxBleConnection, string, CancellationToken, Task> Zig(byte[] cmd)
            => (conn, g, t) => conn.SendZigbeeCommandToAsync(g, mn, mp, dest, cmd, t);

        // Exactly one channel is set per request (the if/else chain in the public endpoints). Map it to
        // the queue channel + the GATT write + the advert-confirmation predicate (relay-verify), then
        // enqueue and return 202 — the write happens async.
        BleChannel channel;
        Func<IAwoxBleConnection, string, CancellationToken, Task> send;
        Func<LightState, bool>? expected;
        var powerOff = false;

        if (on is not null)
        {
            channel = BleChannel.Power;
            powerOff = !on.Value;
            send = zigbee ? Zig(AwoxMeshProtocol.ZigbeePowerCommand(on.Value))
                          : Tlmesh(AwoxMeshProtocol.CmdPower, AwoxMeshProtocol.PowerPayload(on.Value));
            expected = ExpectedStatePredicates.Power(on.Value);
        }
        else if (color is not null)
        {
            channel = BleChannel.Color;
            send = zigbee ? Zig(AwoxMeshProtocol.ZigbeeColorCommand(color.R, color.G, color.B))
                          : Tlmesh(AwoxMeshProtocol.CmdColor, AwoxMeshProtocol.ColorPayload(color.R, color.G, color.B));
            expected = ExpectedStatePredicates.Color(new RgbColor(color.R, color.G, color.B));
        }
        else if (brightnessPercent is not null)
        {
            channel = BleChannel.Brightness;
            send = zigbee ? Zig(AwoxMeshProtocol.ZigbeeBrightnessCommand((byte)Math.Clamp(brightnessPercent.Value * 254 / 100, 1, 254)))
                          : Tlmesh(AwoxMeshProtocol.CmdWhiteBrightness, AwoxMeshProtocol.WhiteBrightnessPayload(brightnessPercent.Value));
            expected = ExpectedStatePredicates.Brightness(brightnessPercent.Value);
        }
        else if (colorBrightnessPercent is not null)
        {
            channel = BleChannel.ColorBrightness;
            // Zigbee has one brightness opcode; tlmesh has a distinct colour-channel one (0xF2).
            send = zigbee ? Zig(AwoxMeshProtocol.ZigbeeBrightnessCommand((byte)Math.Clamp(colorBrightnessPercent.Value * 254 / 100, 1, 254)))
                          : Tlmesh(AwoxMeshProtocol.CmdColorBrightness, AwoxMeshProtocol.ColorBrightnessPayload(colorBrightnessPercent.Value));
            expected = ExpectedStatePredicates.Brightness(colorBrightnessPercent.Value);
        }
        else if (mireds is not null)
        {
            channel = BleChannel.ColorTemp;
            send = zigbee ? Zig(AwoxMeshProtocol.ZigbeeWhiteTempCommand(MiredsToZigbeeWhite(mireds.Value)))
                          : Tlmesh(AwoxMeshProtocol.CmdWhiteTemperature, AwoxMeshProtocol.WhiteTemperaturePayload(mireds.Value));
            expected = ExpectedStatePredicates.ColorTemp(mireds.Value);
        }
        else
        {
            return Accepted(); // nothing addressed — no-op
        }

        // An explicit ?via pins control to a chosen node — no relay decision/verification, send as-is.
        if (forceVia) expected = null;

        // If the lamp is currently OFF and this is a light command (colour/brightness/temp, not a power
        // command), turn it on first — a write to an off bulb may be dropped or land but stay dark. The
        // queue always drains the Power channel before the others, so this on lands ahead of the command.
        if (on is null && IsCurrentlyOff(lamp))
        {
            var powerOn = zigbee ? Zig(AwoxMeshProtocol.ZigbeePowerCommand(true))
                                 : Tlmesh(AwoxMeshProtocol.CmdPower, AwoxMeshProtocol.PowerPayload(true));
            _queue.Enqueue(new BleCommand(gw, lamp.Mac, mn, mp, dest, BleChannel.Power, powerOn, ExpectedStatePredicates.Power(true)));
        }

        // Snapshot whether the target is ALREADY in the wanted state — read NOW, before PersistStateAsync
        // below seeds the cache with this command's own (optimistic) value. The relay coordinator needs the
        // pre-command truth to tell a real, relay-attributable change from a no-op confirmation; reading it
        // after the optimistic write would always say "already there" and short-circuit verification.
        var targetWasInState = expected is not null
            && _stateCache.TryGet(lamp.Mac, out var preState)
            && expected(preState);

        _queue.Enqueue(new BleCommand(gw, lamp.Mac, mn, mp, dest, channel, send, expected, powerOff, targetWasInState));

        // Remember what we just sent so the UI reflects it immediately (these bulbs don't report real
        // state, and the write itself is now asynchronous). A DB failure here is the only thing that can
        // fault the request — surface it as 503, same as before.
        try
        {
            await PersistStateAsync(lamp, on, color, brightnessPercent, colorBrightnessPercent, mireds, ct);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }

        return Accepted();
    }

    /// <summary>
    /// Whether the lamp is currently displaying colour (vs white). The authoritative source is the live
    /// advert cache (the lamp broadcasts its mode); when no advert has been seen yet (e.g. just started,
    /// or off-Linux) we fall back to the last persisted command — a colour having been set. We err
    /// toward colour because the bug we're guarding against is an unwanted flip TO white.
    /// </summary>
    private bool IsCurrentlyColorMode(LampDevice lamp)
    {
        if (!string.IsNullOrWhiteSpace(lamp.Mac) && _stateCache.TryGet(lamp.Mac, out var live))
            return live.IsColorMode;
        return LampStateDto.Parse(lamp.LastState)?.Color is not null;
    }

    /// <summary>Whether the lamp is KNOWN to be off (live cache first, else last persisted state). Returns
    /// false when state is unknown — we only prepend an explicit power-on when we're sure it's off.</summary>
    private bool IsCurrentlyOff(LampDevice lamp)
    {
        if (!string.IsNullOrWhiteSpace(lamp.Mac) && _stateCache.TryGet(lamp.Mac, out var live))
            return !live.IsOn;
        return LampStateDto.Parse(lamp.LastState)?.On == false;
    }

    /// <summary>Merges the just-sent command into the lamp's last-known state and persists it.</summary>
    private async Task PersistStateAsync(LampDevice lamp, bool? on, ColorRequest? color,
        int? brightness, int? colorBrightness, int? mireds, CancellationToken ct)
    {
        var st = LampStateDto.Parse(lamp.LastState) ?? new LampStateDto();
        if (on is not null) st = st with { On = on };
        // Any colour/brightness/temp command implies the lamp is on (it lights up).
        // Colour and white-temp are mutually exclusive modes: setting one clears the other, so the
        // last-known state (and IsCurrentlyColorMode's fallback) reflects the CURRENT mode and a later
        // plain dim picks the right brightness channel. Without clearing, a stale Color survives a switch
        // to white and the next dim flips a white lamp back to colour (Connect-C).
        if (color is not null) st = st with { On = true, Color = new RgbDto(color.R, color.G, color.B), ColorTemp = null };
        if (brightness is not null) st = st with { On = true, Brightness = brightness };
        if (colorBrightness is not null) st = st with { On = true, ColorBrightness = colorBrightness };
        if (mireds is not null) st = st with { On = true, ColorTemp = mireds, Color = null };

        // HA exposes a SINGLE brightness, and these lamps behave as one dimmer across white + colour.
        // Keeping two divergent stored fields (white Brightness vs ColorBrightness) lets a stale value
        // resurface on a mode switch and flip the reported brightness (slider jumps 20→60→20). So mirror
        // the two fields for every lamp EXCEPT genuine separate-channel ones (SeparateWhiteColor, e.g.
        // Rovito Z) which intentionally show independent white/colour brightness. Reporting only — the
        // command path still drives the correct white (0xF1) or colour (0xF2) channel for the mode.
        if (!lamp.SeparateWhiteColor && (brightness ?? colorBrightness) is { } b)
            st = st with { Brightness = b, ColorBrightness = b };

        lamp.LastState = st.ToJson();
        await _store.UpdateLampAsync(lamp, ct);

        // Keep the cache's colour/white mode accurate: a colour (or colour-brightness) command leaves the
        // lamp in colour mode, a white-temp command in white mode; a plain power/dim command changes
        // neither, so PRESERVE the current mode rather than clobbering it to false. This is what a later
        // plain dim reads back via IsCurrentlyColorMode to pick the right brightness channel — without
        // this, every command reset the mode to false and the dim flipped a colour lamp to white.
        bool isColorMode;
        if (color is not null || colorBrightness is not null) isColorMode = true;
        else if (mireds is not null) isColorMode = false;
        else if (!string.IsNullOrWhiteSpace(lamp.Mac) && _stateCache.TryGet(lamp.Mac, out var cur)) isColorMode = cur.IsColorMode;
        else isColorMode = st.Color is not null;

        // Push the change live (SignalR) keyed by the lamp MAC — the same id the SmartHome BFF relay
        // maps to the device registry. These bulbs don't report real state, so without this the only
        // live source would be the (Connect-Z-only) advert scan; this keeps every client in sync the
        // moment a command lands, regardless of which client sent it.
        _notifier.NotifyStateChanged(lamp.Mac, new LightState
        {
            IsOn = st.On ?? false,
            BrightnessPercent = st.Brightness ?? st.ColorBrightness ?? 0,
            ColorTempMireds = st.ColorTemp,
            Color = st.Color is { } c ? new RgbColor((byte)c.R, (byte)c.G, (byte)c.B) : default,
            IsColorMode = isColorMode,
            LastUpdatedUtc = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Maps mireds (153 cold .. 500 warm) to the Connect-Z white-temp byte. Direction confirmed on
    /// hardware: low = cold, high = warm, so warm mireds → high value. Scale 0..0xFF (usable limits
    /// not yet calibrated — see the ble-implementation-progress notes).
    /// </summary>
    private static byte MiredsToZigbeeWhite(int mireds)
    {
        mireds = Math.Clamp(mireds, 153, 500);
        var v = (int)Math.Round((mireds - 153) / (500.0 - 153) * 255);
        return (byte)Math.Clamp(v, 0, 255);
    }

    private async Task<LampDevice?> ResolveLampAsync(string key, CancellationToken ct)
    {
        if (int.TryParse(key, out var id))
            return await _store.GetLampByIdAsync(id, ct);
        return await _store.GetLampByNameAsync(key, ct)
            ?? await _store.GetLampByMacAsync(key.ToLowerInvariant(), ct);
    }

    // ---- DTOs ----------------------------------------------------------------------------------

    // Contract-conformant device shape (SmartHome.Contracts control-api): the leading fields are the
    // generic contract (id as string, "type", "capabilities", "reachable"); the trailing fields are
    // AwoX-specific extras the generic BFF/frontend simply ignore but the admin UI uses.
    public sealed record LampDto(
        string Id, string Name, string Category, string? Room, string? Type, string[] Capabilities, bool? Reachable,
        LampStateDto? State, string Mac, int MeshId, LightProtocol Protocol, string Model, bool Enabled,
        bool SeparateWhiteColor, string? Mesh)
    {
        public static LampDto From(LampDevice l, ILightStateCache? cache = null, TimeSpan? onlineWithin = null) => new(
            Id: l.Id.ToString(),
            Name: l.Name,
            Category: CategoryFor(l),
            Room: l.Room,
            Type: l.DeviceType,
            Capabilities: CapabilitiesFor(l),
            Reachable: ReachableFor(l, cache, onlineWithin),
            State: StateFor(l, cache),
            Mac: l.Mac, MeshId: l.MeshId, Protocol: l.Protocol, Model: l.Model,
            Enabled: l.Enabled, SeparateWhiteColor: l.SeparateWhiteColor, Mesh: l.Mesh?.Service);

        /// <summary>
        /// Whether the lamp is currently reachable: true when it's been seen (a live advert, or a command)
        /// within <paramref name="onlineWithin"/>. A lamp that has never been observed this run, or whose
        /// last sighting is older than the window, is treated as offline — these lamps usually just have no
        /// power. null only when we have no cache to judge by. The persisted last-known state does NOT
        /// count as a sighting (it can be stale from a previous run).
        /// </summary>
        private static bool? ReachableFor(LampDevice l, ILightStateCache? cache, TimeSpan? onlineWithin)
        {
            if (cache is null || string.IsNullOrWhiteSpace(l.Mac)) return null;
            return cache.IsFresh(l.Mac, onlineWithin ?? TimeSpan.FromSeconds(AppSettingKeys.BleOfflineAfterSecondsDefault));
        }

        /// <summary>
        /// The lamp's state for the API: the persisted last-known state, overlaid with the live in-memory
        /// cache (advert scan / latest command) when present — so untouched lamps still show their real
        /// current state. The cache carries no white-vs-colour brightness split, so any persisted
        /// ColorBrightness is kept.
        /// </summary>
        private static LampStateDto? StateFor(LampDevice l, ILightStateCache? cache)
        {
            var state = LampStateDto.Parse(l.LastState);
            if (cache is null || string.IsNullOrWhiteSpace(l.Mac) || !cache.TryGet(l.Mac, out var live))
                return state;

            return (state ?? new LampStateDto()) with
            {
                On = live.IsOn,
                Brightness = live.BrightnessPercent,
                Color = new RgbDto(live.Color.R, live.Color.G, live.Color.B),
                ColorTemp = live.ColorTempMireds,
            };
        }

        private static readonly string[] RemoteHints = { "fernbedien", "remote" };
        private static readonly string[] SwitchHints = { "switch", "schalter", "taster" };

        /// <summary>Contract category for the frontend's domain nav (light / switch / remote).</summary>
        private static string CategoryFor(LampDevice l)
        {
            var hay = $"{l.DeviceType} {l.Name} {l.Model}".ToLowerInvariant();
            if (RemoteHints.Any(hay.Contains)) return "remote";
            if (SwitchHints.Any(hay.Contains)) return "switch";
            return "light";
        }

        /// <summary>Contract capabilities. Empty ⇒ read-only (remote/switch). Separate-channel bulbs
        /// (Rovito Z etc.) additionally advertise "colorBrightness".</summary>
        private static string[] CapabilitiesFor(LampDevice l)
        {
            if (CategoryFor(l) is "remote" or "switch")
                return Array.Empty<string>();

            return l.SeparateWhiteColor
                ? new[] { "power", "dim", "colorBrightness", "color", "colorTemp" }
                : new[] { "power", "dim", "color", "colorTemp" };
        }
    }

    public sealed record MeshDto(int Id, string Service, string MeshName)
    {
        public static MeshDto From(MeshNetwork m) => new(m.Id, m.Service, m.MeshName);
    }

    public sealed record AddLampRequest(string Name, string Mac, int MeshId, LightProtocol Protocol,
        string? Model, string? DeviceType, string? Room, int? MeshNetworkId, bool? SeparateWhiteColor);

    public sealed record UpdateLampRequest(string? Name, string? DeviceType, string? Room, int? MeshId,
        bool? Enabled, bool? SeparateWhiteColor);

    public sealed record CloudImportRequest(string Email, string Password);

    public sealed record ImportResultDto(int Meshes, int LampsAdded, int LampsUpdated, int LampsSeen);

    public sealed record ColorRequest(byte R, byte G, byte B);

    public sealed record PercentRequest(int Percent);

    public sealed record MiredsRequest(int Mireds);

    public sealed record RgbDto(int R, int G, int B);

    /// <summary>Last-commanded device state, persisted as JSON in <see cref="LampDevice.LastState"/>.</summary>
    public sealed record LampStateDto(
        bool? On = null, int? Brightness = null, int? ColorBrightness = null, RgbDto? Color = null, int? ColorTemp = null)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public static LampStateDto? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<LampStateDto>(json, JsonOpts); }
            catch (JsonException) { return null; }
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    }
}
