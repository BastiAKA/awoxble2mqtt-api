using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AwoxController.Zigbee;

/// <summary>
/// Controls Zigbee lights through Zigbee2MQTT. Sends commands to
/// "<base>/<friendly_name>/set", caches state published on "<base>/<friendly_name>",
/// and builds the device list from the retained "<base>/bridge/devices" message.
///
/// Registered as IHostedService as well so it is constructed at startup and its
/// MQTT event handler is wired before any retained messages arrive.
/// </summary>
public sealed class ZigbeeLightService : ILightBackend, IHostedService
{
    private const int ZigbeeMaxBrightness = 254; // Zigbee2MQTT brightness range is 0-254

    private readonly Zigbee2MqttConnection _mqtt;
    private readonly ILogger<ZigbeeLightService> _logger;

    private readonly ConcurrentDictionary<string, LightState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LightDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ZigbeeLightService(Zigbee2MqttConnection mqtt, ILogger<ZigbeeLightService> logger)
    {
        _mqtt = mqtt;
        _logger = logger;
        _mqtt.MessageReceived += OnMessageAsync;
    }

    public LightTransport Transport => LightTransport.Zigbee;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _mqtt.MessageReceived -= OnMessageAsync;
        return Task.CompletedTask;
    }

    // ---- ILightService: commands ---------------------------------------------------------

    public IReadOnlyCollection<LightDevice> GetDevices() => _devices.Values.ToList();

    public bool TryGetState(string deviceId, out LightState state) => _states.TryGetValue(deviceId, out state!);

    public Task SetPowerAsync(string deviceId, bool on, CancellationToken ct = default)
        => PublishSetAsync(deviceId, new { state = on ? "ON" : "OFF" }, ct);

    public Task ToggleAsync(string deviceId, CancellationToken ct = default)
        => PublishSetAsync(deviceId, new { state = "TOGGLE" }, ct);

    public Task SetBrightnessAsync(string deviceId, int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        var zigbee = (int)Math.Round(percent / 100.0 * ZigbeeMaxBrightness);
        return PublishSetAsync(deviceId, new { brightness = zigbee }, ct);
    }

    public Task SetColorAsync(string deviceId, RgbColor color, CancellationToken ct = default)
        => PublishSetAsync(deviceId, new { color = new { r = color.R, g = color.G, b = color.B } }, ct);

    public Task SetColorTemperatureAsync(string deviceId, int mireds, CancellationToken ct = default)
    {
        mireds = Math.Clamp(mireds, 153, 500);
        return PublishSetAsync(deviceId, new { color_temp = mireds }, ct);
    }

    private Task PublishSetAsync(string deviceId, object payload, CancellationToken ct)
    {
        var topic = $"{_mqtt.BaseTopic}/{deviceId}/set";
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        _logger.LogDebug("→ {Topic} {Json}", topic, json);
        return _mqtt.PublishAsync(topic, json, ct);
    }

    // ---- Inbound message routing ---------------------------------------------------------

    private Task OnMessageAsync(string topic, string payload)
    {
        var prefix = _mqtt.BaseTopic + "/";
        if (!topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        var sub = topic[prefix.Length..]; // e.g. "kitchen_lamp" or "bridge/devices"

        if (sub.Equals("bridge/devices", StringComparison.OrdinalIgnoreCase))
        {
            HandleDeviceList(payload);
            return Task.CompletedTask;
        }

        // Ignore other bridge/* topics and our own /set and /get echoes.
        if (sub.StartsWith("bridge/", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (sub.EndsWith("/set", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (sub.EndsWith("/get", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        HandleStateUpdate(sub, payload);
        return Task.CompletedTask;
    }

    private void HandleStateUpdate(string friendlyName, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var state = _states.GetOrAdd(friendlyName, _ => new LightState());

            if (root.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                state.IsOn = string.Equals(st.GetString(), "ON", StringComparison.OrdinalIgnoreCase);

            if (root.TryGetProperty("brightness", out var br) && br.TryGetInt32(out var b))
                state.BrightnessPercent = (int)Math.Round(b / (double)ZigbeeMaxBrightness * 100.0);

            if (root.TryGetProperty("color_temp", out var ctEl) && ctEl.TryGetInt32(out var mireds))
                state.ColorTempMireds = mireds;

            if (root.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.Object &&
                col.TryGetProperty("r", out var rEl) &&
                col.TryGetProperty("g", out var gEl) &&
                col.TryGetProperty("b", out var bEl) &&
                rEl.TryGetInt32(out var r) && gEl.TryGetInt32(out var g) && bEl.TryGetInt32(out var bl))
            {
                state.Color = new RgbColor((byte)r, (byte)g, (byte)bl);
            }

            state.LastUpdatedUtc = DateTime.UtcNow;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse state for {Device}", friendlyName);
        }
    }

    private void HandleDeviceList(string payload)
    {
        try
        {
            var devices = JsonSerializer.Deserialize<List<Zigbee2MqttDevice>>(payload, JsonOpts) ?? new();
            foreach (var d in devices)
            {
                if (string.IsNullOrEmpty(d.FriendlyName)) continue;
                if (string.Equals(d.Type, "Coordinator", StringComparison.OrdinalIgnoreCase)) continue;

                _devices[d.FriendlyName] = new LightDevice
                {
                    Id = d.FriendlyName,
                    FriendlyName = d.FriendlyName,
                    Model = d.Definition?.Model ?? d.ModelId ?? "unknown",
                    Manufacturer = d.Definition?.Vendor ?? d.Manufacturer ?? "unknown",
                    Transport = LightTransport.Zigbee
                };
            }

            _logger.LogInformation("Discovered {Count} Zigbee device(s).", _devices.Count);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse bridge/devices payload.");
        }
    }

    // ---- Minimal DTOs for the bridge/devices payload (snake_case from Zigbee2MQTT) -------

    private sealed class Zigbee2MqttDevice
    {
        [JsonPropertyName("friendly_name")] public string? FriendlyName { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("model_id")] public string? ModelId { get; set; }
        [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
        [JsonPropertyName("definition")] public Zigbee2MqttDefinition? Definition { get; set; }
    }

    private sealed class Zigbee2MqttDefinition
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    }
}
