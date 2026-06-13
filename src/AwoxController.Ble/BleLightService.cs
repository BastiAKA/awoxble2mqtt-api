using System.Collections.Concurrent;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AwoxController.Ble;

/// <summary>
/// AwoX BLE mesh backend. Exposes the configured bulbs as light devices and translates the
/// transport-agnostic <see cref="ILightService"/> calls into AwoX mesh command packets sent over
/// a single <see cref="AwoxBleConnection"/> (the gateway bulb relays them to the whole mesh).
///
/// When disabled (the default) it registers no devices and the CompositeLightService never routes
/// to it. The connection is platform-gated, so this builds and runs on non-Linux dev machines.
/// </summary>
public sealed class BleLightService : ILightBackend, IHostedService
{
    // AwoX native ranges (see python-awox-mesh-light).
    private const int WhiteBrightnessMin = 0x01, WhiteBrightnessMax = 0x7F;
    private const int ColorBrightnessMin = 0x0A, ColorBrightnessMax = 0x64;
    private const int WhiteTempMax = 0x7F;
    private const int MiredsCold = 153, MiredsWarm = 500;

    private readonly AwoxBleOptions _options;
    private readonly ILogger<BleLightService> _logger;

    private readonly ConcurrentDictionary<string, AwoxBleDevice> _config = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LightDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LightState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _colorMode = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _published = new(StringComparer.OrdinalIgnoreCase);

    private readonly IAwoxBleConnection _connection;
    private readonly ILightStateNotifier? _notifier;
    private readonly ILightStateCache? _cache;
    private readonly IBleAdvertStream? _advertStream;

    public BleLightService(
        IOptions<AwoxBleOptions> options, IAwoxBleConnection connection, ILogger<BleLightService> logger,
        ILightStateNotifier? notifier = null, ILightStateCache? cache = null, IBleAdvertStream? advertStream = null)
    {
        _options = options.Value;
        _connection = connection;
        _logger = logger;
        _notifier = notifier;
        _cache = cache;
        _advertStream = advertStream;
    }

    public LightTransport Transport => LightTransport.Bluetooth;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AwoX BLE backend disabled (AwoxBle:Enabled = false).");
            return Task.CompletedTask;
        }

        foreach (var d in _options.Devices)
        {
            if (string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Mac))
                continue;

            _config[d.Name] = d;
            _devices[d.Name] = new LightDevice
            {
                Id = d.Name,
                FriendlyName = d.Name,
                Model = d.Model,
                Manufacturer = "AwoX",
                Transport = LightTransport.Bluetooth
            };
        }

        _connection.StatusReceived += OnStatusReceived;

        // No eager connect at startup: bulbs are powered on/off and come and go from range.
        // The link to a bulb is established lazily on the first command that addresses it, so an
        // offline bulb is never a startup blocker — it just makes that one command fail cleanly.
        _logger.LogInformation(
            "AwoX BLE backend configured with {Count} device(s). Connecting on demand (no startup connect).",
            _devices.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyCollection<LightDevice> GetDevices() => _devices.Values.ToList();

    public bool TryGetState(string deviceId, out LightState state) => _states.TryGetValue(deviceId, out state!);

    // ---- Commands -------------------------------------------------------------------------

    public async Task SetPowerAsync(string deviceId, bool on, CancellationToken ct = default)
    {
        var dest = DestId(deviceId);
        await Connection().SendCommandAsync(dest, AwoxMeshProtocol.CmdPower, [(byte)(on ? 0x01 : 0x00)], ct);
        StateOf(deviceId).IsOn = on;
        Touch(deviceId);
    }

    public Task ToggleAsync(string deviceId, CancellationToken ct = default)
    {
        var on = _states.TryGetValue(deviceId, out var s) && s.IsOn;
        return SetPowerAsync(deviceId, !on, ct);
    }

    public async Task SetBrightnessAsync(string deviceId, int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        var dest = DestId(deviceId);

        if (_colorMode.TryGetValue(deviceId, out var color) && color)
        {
            var value = Scale(percent, ColorBrightnessMin, ColorBrightnessMax);
            await Connection().SendCommandAsync(dest, AwoxMeshProtocol.CmdColorBrightness, [(byte)value], ct);
        }
        else
        {
            var value = Scale(percent, WhiteBrightnessMin, WhiteBrightnessMax);
            await Connection().SendCommandAsync(dest, AwoxMeshProtocol.CmdWhiteBrightness, [(byte)value], ct);
        }

        StateOf(deviceId).BrightnessPercent = percent;
        Touch(deviceId);
    }

    public async Task SetColorAsync(string deviceId, RgbColor color, CancellationToken ct = default)
    {
        var dest = DestId(deviceId);
        await Connection().SendCommandAsync(dest, AwoxMeshProtocol.CmdColor, [0x04, color.R, color.G, color.B], ct);
        _colorMode[deviceId] = true;
        var s = StateOf(deviceId);
        s.Color = color;
        s.IsOn = true;
        Touch(deviceId);
    }

    public async Task SetColorTemperatureAsync(string deviceId, int mireds, CancellationToken ct = default)
    {
        mireds = Math.Clamp(mireds, MiredsCold, MiredsWarm);
        var dest = DestId(deviceId);

        // Map mireds (153 cold … 500 warm) to AwoX white temp (0 warm … 0x7F cold).
        // NOTE: this orientation is a best guess and may need inverting after a hardware test.
        var temp = (int)Math.Round((double)(MiredsWarm - mireds) / (MiredsWarm - MiredsCold) * WhiteTempMax);
        temp = Math.Clamp(temp, 0, WhiteTempMax);

        await Connection().SendCommandAsync(dest, AwoxMeshProtocol.CmdWhiteTemperature, [(byte)temp], ct);
        _colorMode[deviceId] = false;
        StateOf(deviceId).ColorTempMireds = mireds;
        Touch(deviceId);
    }

    // ---- Inbound status -------------------------------------------------------------------

    private void OnStatusReceived(byte[] message)
    {
        // Decrypted layout (see python-awox-mesh-light parseStatusResult):
        //   [3]=mesh id, [12]=mode, [13]=white brightness, [14]=white temp,
        //   [15]=color brightness, [16..18]=r,g,b. status = mode % 2 (on/off).
        if (message.Length < 19) return;

        var meshId = message[3];
        var mode = message[12];
        if (mode >= 40) return; // filter non-state notifications

        var device = ResolveByMeshId(meshId);
        if (device is null) return;

        var state = StateOf(device);
        state.IsOn = (mode % 2) == 1;
        _colorMode[device] = mode == 3 || mode == 7; // 3/7 = color modes, 1/5 = white

        if ((mode % 2) == 1) // only trust the values while the bulb is on
        {
            state.Color = new RgbColor(message[16], message[17], message[18]);
            state.BrightnessPercent = _colorMode[device]
                ? Unscale(message[15], ColorBrightnessMin, ColorBrightnessMax)
                : Unscale(message[13], WhiteBrightnessMin, WhiteBrightnessMax);
        }

        Touch(device);
    }

    /// <summary>
    /// Applies live state decoded from a Connect-Z status <b>advertisement</b> (passive scan — no
    /// connection or login). Called by <see cref="BleAdvStatusService"/> for each configured bulb whose
    /// advert it sees. Unlike the notify path this trusts the values even when the bulb is off, because
    /// the advert retains the last brightness/colour, so the UI can show them while the lamp is off.
    /// </summary>
    public void ApplyAdvertStatus(string deviceId, AwoxAdvertStatus advert)
    {
        var state = StateOf(deviceId);
        state.IsOn = advert.IsOn;
        state.BrightnessPercent = advert.BrightnessPercent;
        state.IsColorMode = advert.IsColorMode;
        _colorMode[deviceId] = advert.IsColorMode;

        if (advert.IsColorMode)
            state.Color = advert.ToRgb();
        else
            state.ColorTempMireds = WhiteTempToMireds(advert.WhiteTemp);

        Touch(deviceId);

        // Refresh the cache's "last seen" stamp on EVERY advert tick, not just on a state change: seeing
        // the advert at all proves the lamp is powered and in range right now, which is exactly what the
        // registry's reachability check reads back. Touch only pushes to SignalR on change (to avoid
        // per-tick spam), so without this the cache timestamp would freeze for a static lamp and it would
        // wrongly look offline. deviceId is the lamp MAC here (the advert scan keys by MAC).
        _cache?.Set(deviceId, state);

        // Publish to the live advert stream so callers can await a lamp reaching a state (relay
        // verification). Every tick, snapshotted — see BleAdvertStream.
        _advertStream?.Publish(deviceId, state);
    }

    // Advert white-temp is a single byte on a 0x00–0xFF scale (NOT the 0–0x7F command scale). The exact
    // scale/direction isn't verified yet, so this is a best-effort linear map onto the mireds range;
    // refine once a cold/warm hardware sweep pins it down. Power/brightness/hue/sat ARE verified.
    private static int WhiteTempToMireds(byte raw)
        => (int)Math.Round(MiredsCold + raw / 255.0 * (MiredsWarm - MiredsCold));

    private string? ResolveByMeshId(byte meshId)
    {
        foreach (var (name, cfg) in _config)
            if (cfg.MeshId == meshId)
                return name;

        // mesh id 0 is the gateway / broadcast: attribute it to the first configured device.
        return meshId == 0 ? _config.Keys.FirstOrDefault() : null;
    }

    // ---- Helpers --------------------------------------------------------------------------

    private IAwoxBleConnection Connection() => _connection;

    private ushort DestId(string deviceId)
        => _config.TryGetValue(deviceId, out var d)
            ? d.MeshId
            : throw new KeyNotFoundException(
                $"Unknown AwoX BLE device '{deviceId}'. Add it to AwoxBle:Devices (Name/Mac/MeshId), " +
                $"or use the direct MAC endpoints POST/PUT /api/ble/{{mac}}/… instead of /api/lights.");

    private LightState StateOf(string deviceId) => _states.GetOrAdd(deviceId, _ => new LightState());

    // Stamp the update time, then push to subscribers ONLY when a meaningful field actually changed.
    // The advert scanner calls this every poll tick, so the change-detection is what keeps the SignalR
    // stream (and the clients) quiet between real changes — no per-tick spam.
    private void Touch(string deviceId)
    {
        var state = StateOf(deviceId);
        state.LastUpdatedUtc = DateTime.UtcNow;
        if (_notifier is null) return;

        var signature = $"{state.IsOn}|{state.BrightnessPercent}|{state.ColorTempMireds}|" +
                        $"{state.Color.R},{state.Color.G},{state.Color.B}";
        if (_published.TryGetValue(deviceId, out var prev) && prev == signature)
            return; // nothing the UI cares about changed

        _published[deviceId] = signature;
        // Push the lamp MAC (not the internal config name) as the id: it's the stable key downstream
        // consumers (the SmartHome BFF relay) match against the device registry on. Fall back to the
        // config name if a device somehow has no MAC configured.
        var pushId = _config.TryGetValue(deviceId, out var cfg) && !string.IsNullOrWhiteSpace(cfg.Mac)
            ? cfg.Mac
            : deviceId;
        _notifier.NotifyStateChanged(pushId, state);
    }

    private static int Scale(int percent, int min, int max)
        => Math.Clamp((int)Math.Round(percent / 100.0 * max), min, max);

    private static int Unscale(int value, int min, int max)
        => Math.Clamp((int)Math.Round((double)value / max * 100.0), 0, 100);
}
