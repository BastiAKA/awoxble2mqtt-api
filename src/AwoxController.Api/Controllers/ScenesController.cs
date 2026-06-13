using AwoxController.Api.Security;
using AwoxController.Api.Services;
using AwoxController.Ble;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AwoxController.Api.Controllers;

using StateDto = DevicesController.LampStateDto; // reuse the one {on,brightness,…} JSON shape

/// <summary>
/// Scenes: a named set of lamps each with a desired state. Create/list/update/delete them, and
/// <c>POST {id}/apply</c> to push every lamp in the scene to its desired state at once.
/// </summary>
[ApiController]
[Route("api/scenes")]
[AuthorizeViaApiKey]
public sealed class ScenesController : ControllerBase
{
    private readonly ISceneStore _scenes;
    private readonly IDeviceStore _devices;
    private readonly IBleCommandQueue _queue;
    private readonly IMeshGatewayResolver _gateways;
    private readonly ILightStateNotifier _notifier;

    public ScenesController(ISceneStore scenes, IDeviceStore devices, IBleCommandQueue queue,
        IMeshGatewayResolver gateways, ILightStateNotifier notifier)
    {
        _scenes = scenes;
        _devices = devices;
        _queue = queue;
        _gateways = gateways;
        _notifier = notifier;
    }

    /// <summary>GET /api/scenes — all scenes with their items.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<SceneDto>> List(CancellationToken ct)
        => (await _scenes.GetScenesAsync(ct)).Select(SceneDto.From).ToList();

    /// <summary>GET /api/scenes/{id}</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<SceneDto>> Get(int id, CancellationToken ct)
    {
        var scene = await _scenes.GetSceneByIdAsync(id, ct);
        return scene is null ? NotFound() : SceneDto.From(scene);
    }

    /// <summary>POST /api/scenes — body { name, items:[{ lampId, desiredState }] }.</summary>
    [HttpPost]
    public async Task<ActionResult<SceneDto>> Create([FromBody] SceneRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Scene name is required.");

        var scene = new Scene { Name = req.Name.Trim(), Items = ToItems(req.Items) };
        var saved = await _scenes.AddSceneAsync(scene, ct);
        // Re-read so the items come back with their lamp names populated.
        return SceneDto.From((await _scenes.GetSceneByIdAsync(saved.Id, ct))!);
    }

    /// <summary>PUT /api/scenes/{id} — replaces name + items.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<SceneDto>> Update(int id, [FromBody] SceneRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Scene name is required.");

        var updated = await _scenes.UpdateSceneAsync(id, req.Name.Trim(), ToItems(req.Items), ct);
        if (updated is null) return NotFound();
        return SceneDto.From((await _scenes.GetSceneByIdAsync(id, ct))!);
    }

    /// <summary>DELETE /api/scenes/{id}</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
        => await _scenes.RemoveSceneAsync(id, ct) ? NoContent() : NotFound();

    /// <summary>
    /// POST /api/scenes/{id}/apply — queues every lamp in the scene to its desired state and returns at
    /// once. Per-lamp "ok" means the command was accepted+queued (the BLE write drains asynchronously);
    /// a lamp that no longer exists or has no mesh is reported as a failure without affecting the others.
    /// </summary>
    [HttpPost("{id:int}/apply")]
    public async Task<ActionResult<ApplyResultDto>> Apply(int id, CancellationToken ct)
    {
        var scene = await _scenes.GetSceneByIdAsync(id, ct);
        if (scene is null) return NotFound();

        // Resolve every item's lamp up front, then drive them GROUPED BY MESH: enqueue all of one mesh's
        // commands before moving to the next. The queue drains in enqueue order and the relay coordinator
        // routes each subsequent same-mesh lamp through the already-held connection — so a scene spanning
        // two meshes does ONE connect per mesh instead of reconnecting on every lamp. Within a mesh,
        // reachable lamps go first so the held anchor is a node that's actually in range and the rest
        // relay through it; an unconfirmed relay still falls back to a direct connect + requeue (the
        // RelayCoordinator/queue retry path we already have).
        var resolved = new List<(SceneItem Item, LampDevice? Lamp)>();
        foreach (var item in scene.Items)
            resolved.Add((item, await _devices.GetLampByIdAsync(item.LampDeviceId, ct)));

        var ordered = resolved
            .OrderBy(r => MeshKey(r.Lamp), StringComparer.Ordinal)                       // group by mesh
            .ThenByDescending(r => r.Lamp is not null && _gateways.IsReachable(r.Lamp))  // reachable anchor first
            .ToList();

        var results = new List<ApplyItemResult>();
        foreach (var (item, lamp) in ordered)
        {
            if (lamp is null)
            {
                results.Add(new ApplyItemResult(item.LampDeviceId, "(deleted)", false, "lamp no longer exists"));
                continue;
            }

            try
            {
                await ApplyToLampAsync(lamp, StateDto.Parse(item.DesiredState) ?? new StateDto(), ct);
                results.Add(new ApplyItemResult(lamp.Id, lamp.Name, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new ApplyItemResult(lamp.Id, lamp.Name, false, ex.Message));
            }
        }

        return new ApplyResultDto(scene.Id, scene.Name, results);
    }

    /// <summary>
    /// Pushes one lamp to a full desired state by enqueuing its commands. The queue drains them in a
    /// mode-preserving order (power → colour|white-temp → brightness), so dimming uses the colour-
    /// brightness opcode (0xF2) when the scene sets a colour and never flips the lamp to white. The
    /// commands are queued (not awaited): a scene touching several lamps in one room then drains over a
    /// single held gateway link instead of a connect per lamp.
    /// </summary>
    private async Task ApplyToLampAsync(LampDevice lamp, StateDto want, CancellationToken ct)
    {
        if (lamp.Mesh is null)
            throw new InvalidOperationException($"Lamp '{lamp.Name}' has no mesh assigned.");

        var (mn, mp) = (lamp.Mesh.MeshName, lamp.Mesh.MeshPassword);
        var dest = (ushort)lamp.MeshId;
        var zigbee = lamp.Protocol == LightProtocol.Zigbee;
        // Connect through a reachable mesh node (the target itself when online, else a reachable sibling)
        // so a scene doesn't stall on a long failed connect to an offline lamp; relay to the target by id.
        var gw = await _gateways.ResolveGatewayAsync(lamp, ct);

        // The queue worker supplies its own token at send time — never the request ct (cancelled the
        // moment Apply returns). The GATT write takes the gateway MAC as a parameter so the relay
        // coordinator can route it through a held node (relay) or directly to the target.
        Func<IAwoxBleConnection, string, CancellationToken, Task> Tlmesh(byte cmd, byte[] data)
            => (conn, g, t) => conn.SendCommandToAsync(g, mn, mp, dest, cmd, data, t);
        Func<IAwoxBleConnection, string, CancellationToken, Task> Zig(byte[] cmd)
            => (conn, g, t) => conn.SendZigbeeCommandToAsync(g, mn, mp, dest, cmd, t);

        void Enqueue(BleChannel channel, Func<IAwoxBleConnection, string, CancellationToken, Task> send,
            Func<LightState, bool>? expected = null, bool powerOff = false)
            => _queue.Enqueue(new BleCommand(gw, lamp.Mac, mn, mp, dest, channel, send, expected, powerOff));

        // Explicit OFF: just power down (the queue discards any colour/brightness queued for this lamp).
        if (want.On == false)
        {
            Enqueue(BleChannel.Power, zigbee ? Zig(AwoxMeshProtocol.ZigbeePowerCommand(false))
                                             : Tlmesh(AwoxMeshProtocol.CmdPower, AwoxMeshProtocol.PowerPayload(false)),
                    ExpectedStatePredicates.Power(false), powerOff: true);
            await PersistAndNotifyAsync(lamp, want, ct);
            return;
        }

        // Power on (any colour/brightness command implies on anyway, but be explicit).
        Enqueue(BleChannel.Power, zigbee ? Zig(AwoxMeshProtocol.ZigbeePowerCommand(true))
                                         : Tlmesh(AwoxMeshProtocol.CmdPower, AwoxMeshProtocol.PowerPayload(true)),
                ExpectedStatePredicates.Power(true));

        // colour set ⇒ colour mode; else colourTemp set ⇒ white mode. This decides the brightness channel.
        var colourMode = want.Color is not null;

        if (want.Color is { } c)
        {
            Enqueue(BleChannel.Color, zigbee ? Zig(AwoxMeshProtocol.ZigbeeColorCommand((byte)c.R, (byte)c.G, (byte)c.B))
                                             : Tlmesh(AwoxMeshProtocol.CmdColor, AwoxMeshProtocol.ColorPayload((byte)c.R, (byte)c.G, (byte)c.B)),
                    ExpectedStatePredicates.Color(new RgbColor((byte)c.R, (byte)c.G, (byte)c.B)));
        }
        else if (want.ColorTemp is { } mireds)
        {
            Enqueue(BleChannel.ColorTemp, zigbee ? Zig(AwoxMeshProtocol.ZigbeeWhiteTempCommand(MiredsToZigbeeWhite(mireds)))
                                                 : Tlmesh(AwoxMeshProtocol.CmdWhiteTemperature, AwoxMeshProtocol.WhiteTemperaturePayload(mireds)),
                    ExpectedStatePredicates.ColorTemp(mireds));
        }

        // Brightness on the channel that matches the mode (see the method summary).
        var bri = want.ColorBrightness ?? want.Brightness;
        if (bri is { } pct)
        {
            var asColour = colourMode || want.ColorBrightness is not null;
            var briExpected = ExpectedStatePredicates.Brightness(pct);
            if (zigbee)
                Enqueue(asColour ? BleChannel.ColorBrightness : BleChannel.Brightness,
                        Zig(AwoxMeshProtocol.ZigbeeBrightnessCommand((byte)Math.Clamp(pct * 254 / 100, 1, 254))), briExpected);
            else if (asColour)
                Enqueue(BleChannel.ColorBrightness, Tlmesh(AwoxMeshProtocol.CmdColorBrightness, AwoxMeshProtocol.ColorBrightnessPayload(pct)), briExpected);
            else
                Enqueue(BleChannel.Brightness, Tlmesh(AwoxMeshProtocol.CmdWhiteBrightness, AwoxMeshProtocol.WhiteBrightnessPayload(pct)), briExpected);
        }

        await PersistAndNotifyAsync(lamp, want, ct);
    }

    /// <summary>Persists the applied state as the lamp's LastState and pushes it live (same as a command).</summary>
    private async Task PersistAndNotifyAsync(LampDevice lamp, StateDto want, CancellationToken ct)
    {
        lamp.LastState = want.ToJson();
        await _devices.UpdateLampAsync(lamp, ct);
        _notifier.NotifyStateChanged(lamp.Mac, new LightState
        {
            IsOn = want.On ?? true,
            BrightnessPercent = want.Brightness ?? want.ColorBrightness ?? 0,
            ColorTempMireds = want.ColorTemp,
            Color = want.Color is { } c ? new RgbColor((byte)c.R, (byte)c.G, (byte)c.B) : default,
            IsColorMode = want.Color is not null,
            LastUpdatedUtc = DateTime.UtcNow,
        });
    }

    // Same mapping DevicesController uses (153 cold .. 500 warm → 0..0xFF, low=cold/high=warm).
    private static byte MiredsToZigbeeWhite(int mireds)
        => (byte)Math.Clamp((int)Math.Round((mireds - 153) / (500.0 - 153.0) * 255.0), 0, 255);

    // Groups scene items by their lamp's mesh (name + password). The NUL separator keeps "a"+"bc" from
    // colliding with "ab"+"c". Null-mesh lamps sort last — they can't be driven (ApplyToLampAsync throws)
    // and are reported as per-item failures without disturbing the real mesh groups.
    private static string MeshKey(LampDevice? lamp)
        => lamp?.Mesh is { } m ? $"{m.MeshName}\u0000{m.MeshPassword}" : "\uffff";

    private static List<SceneItem> ToItems(IEnumerable<SceneItemRequest>? items)
        => (items ?? Enumerable.Empty<SceneItemRequest>())
            .Select(i => new SceneItem { LampDeviceId = i.LampId, DesiredState = (i.DesiredState ?? new StateDto()).ToJson() })
            .ToList();

    // ---- DTOs -------------------------------------------------------------------------------

    public sealed record SceneRequest(string Name, List<SceneItemRequest>? Items);

    public sealed record SceneItemRequest(int LampId, StateDto? DesiredState);

    public sealed record SceneDto(int Id, string Name, DateTime CreatedUtc, IReadOnlyList<SceneItemDto> Items)
    {
        public static SceneDto From(Scene s) => new(
            s.Id, s.Name, s.CreatedUtc,
            s.Items.Select(i => new SceneItemDto(
                i.LampDeviceId, i.Lamp?.Name ?? "(deleted)", StateDto.Parse(i.DesiredState) ?? new StateDto())).ToList());
    }

    public sealed record SceneItemDto(int LampId, string LampName, StateDto DesiredState);

    public sealed record ApplyResultDto(int Id, string Name, IReadOnlyList<ApplyItemResult> Items);

    public sealed record ApplyItemResult(int LampId, string LampName, bool Ok, string? Error);
}
