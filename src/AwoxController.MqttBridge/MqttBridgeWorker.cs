using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace AwoxController.MqttBridge;

/// <summary>
/// The bridge. Publishes the API's lamps/scenes to MQTT in HA-discovery form (retained, so HA picks them
/// up whenever it starts), mirrors live state from the API's SignalR hub onto the MQTT state topics, and
/// translates inbound HA commands back into REST calls. One long-lived MQTT connection + one hub
/// connection; a slow periodic poll only refreshes the device list/availability (live changes are pushed).
/// </summary>
public sealed class MqttBridgeWorker : BackgroundService
{
    private readonly AwoxApiClient _api;
    private readonly HaDiscovery _ha;
    private readonly BridgeOptions _options;
    private readonly ILogger<MqttBridgeWorker> _logger;

    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _mqtt;
    private readonly MqttClientOptions _mqttOptions;
    private readonly HubConnection _hub;

    private readonly ConcurrentDictionary<string, string> _uidToMac = new();   // light uid → MAC
    private readonly ConcurrentDictionary<string, bool> _colorMode = new();    // light uid → in colour mode
    private CancellationToken _ct;

    public MqttBridgeWorker(AwoxApiClient api, HaDiscovery ha, IOptions<BridgeOptions> options, ILogger<MqttBridgeWorker> logger)
    {
        _api = api;
        _ha = ha;
        _options = options.Value;
        _logger = logger;

        _mqtt = _factory.CreateMqttClient();
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Mqtt.Host, _options.Mqtt.Port)
            .WithClientId(_options.Mqtt.ClientId)
            .WithCleanSession();
        if (!string.IsNullOrWhiteSpace(_options.Mqtt.Username))
            b = b.WithCredentials(_options.Mqtt.Username, _options.Mqtt.Password);
        _mqttOptions = b.Build();
        _mqtt.ApplicationMessageReceivedAsync += OnMqttMessageAsync;
        _mqtt.DisconnectedAsync += OnMqttDisconnectedAsync;

        _hub = new HubConnectionBuilder()
            .WithUrl(_options.ApiBaseUrl.TrimEnd('/') + "/hubs/lights", o =>
            {
                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    o.Headers.Add("X-Api-Key", _options.ApiKey!);
            })
            .WithAutomaticReconnect()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true)
            .Build();
        _hub.On<StateChanged>("StateChanged", OnStateChangedAsync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ct = stoppingToken;
        await ConnectMqttAsync(stoppingToken);
        _ = ConnectHubWithRetryAsync(stoppingToken); // independent: state mirroring, not required for commands

        // Refresh the device/scene list (discovery + availability) on a slow cadence. Live state arrives
        // over SignalR; this only catches new lamps, removed lamps and reachability flips.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Refresh from API failed; retrying next cycle."); }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _options.RefreshIntervalSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (_mqtt.IsConnected) await _mqtt.DisconnectAsync(); } catch { /* shutting down */ }
        try { await _hub.DisposeAsync(); } catch { /* shutting down */ }
        await base.StopAsync(cancellationToken);
    }

    // ---- MQTT ------------------------------------------------------------------------------------

    private async Task ConnectMqttAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Connecting to MQTT broker {Host}:{Port}.", _options.Mqtt.Host, _options.Mqtt.Port);
            await _mqtt.ConnectAsync(_mqttOptions, ct);
            var sub = _factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(_ha.LightCommandFilter).WithAtLeastOnceQoS())
                .WithTopicFilter(f => f.WithTopic(_ha.SceneCommandFilter).WithAtLeastOnceQoS())
                .Build();
            await _mqtt.SubscribeAsync(sub, ct);
            _logger.LogInformation("MQTT connected; subscribed to {Light} and {Scene}.", _ha.LightCommandFilter, _ha.SceneCommandFilter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT connect failed; will retry on the disconnect cycle.");
        }
    }

    private async Task OnMqttDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (_ct.IsCancellationRequested) return;
        _logger.LogWarning("MQTT disconnected ({Reason}); reconnecting in {Delay}s.", args.Reason, _options.Mqtt.ReconnectDelaySeconds);
        try { await Task.Delay(TimeSpan.FromSeconds(_options.Mqtt.ReconnectDelaySeconds), _ct); }
        catch (OperationCanceledException) { return; }
        await ConnectMqttAsync(_ct);
        // Re-assert discovery + state after a reconnect (broker may have dropped non-retained session state).
        try { await RefreshAsync(_ct); } catch { /* next cycle */ }
    }

    private async Task OnMqttMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
        try
        {
            if (_ha.LightUidFromCommandTopic(topic) is { } uid)
                await HandleLightCommandAsync(uid, payload);
            else if (_ha.SceneIdFromCommandTopic(topic) is { } sceneId
                     && string.Equals(payload.Trim(), HaDiscovery.ScenePayloadOn, StringComparison.OrdinalIgnoreCase))
                await _api.ApplySceneAsync(sceneId, _ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handling MQTT command on {Topic} failed.", topic);
        }
    }

    private async Task HandleLightCommandAsync(string uid, string json)
    {
        if (!_uidToMac.TryGetValue(uid, out var mac))
        {
            _logger.LogDebug("Command for unknown light uid {Uid} (not yet discovered); ignoring.", uid);
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
        if (string.Equals(state, "OFF", StringComparison.OrdinalIgnoreCase))
        {
            await _api.OffAsync(mac, _ct);
            return;
        }

        var hasColor = root.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.Object;
        var hasTemp = root.TryGetProperty("color_temp", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number;
        var hasBri = root.TryGetProperty("brightness", out var brEl) && brEl.ValueKind == JsonValueKind.Number;

        // Colour/temp set the mode; do them before brightness so the API picks the right brightness channel.
        if (hasColor && col.TryGetProperty("r", out var r) && col.TryGetProperty("g", out var g) && col.TryGetProperty("b", out var bb))
        {
            await _api.ColorAsync(mac, r.GetInt32(), g.GetInt32(), bb.GetInt32(), _ct);
            _colorMode[uid] = true;
        }
        else if (hasTemp)
        {
            await _api.ColorTempAsync(mac, Math.Clamp(ctEl.GetInt32(), 153, 500), _ct);
            _colorMode[uid] = false;
        }

        if (hasBri)
        {
            var pct = Math.Clamp((int)Math.Round(brEl.GetInt32() / 255.0 * 100.0), 1, 100);
            // Brightness goes on the channel matching the mode: explicit from this message, else the last
            // known mode (colour bulbs dim on the colour channel, white on the white channel).
            var asColour = hasColor || (!hasTemp && _colorMode.GetValueOrDefault(uid));
            if (asColour) await _api.ColorBrightnessAsync(mac, pct, _ct);
            else await _api.BrightnessAsync(mac, pct, _ct);
        }
        else if (!hasColor && !hasTemp && string.Equals(state, "ON", StringComparison.OrdinalIgnoreCase))
        {
            await _api.OnAsync(mac, _ct);
        }
    }

    // ---- SignalR (live state in) -----------------------------------------------------------------

    private async Task ConnectHubWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub.StartAsync(ct);
                _logger.LogInformation("SignalR hub connected ({Url}/hubs/lights).", _options.ApiBaseUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalR connect failed; retrying in 5s.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { return; }
            }
        }
    }

    private async Task OnStateChangedAsync(StateChanged sc)
    {
        var uid = HaDiscovery.Uid(sc.DeviceId);
        if (!_uidToMac.ContainsKey(uid)) return; // not a lamp we expose (or not discovered yet)

        var st = sc.State;
        _colorMode[uid] = st.IsColorMode;
        await PublishAsync(_ha.LightAvailabilityTopic(uid), "online", retain: true);
        await PublishAsync(_ha.LightStateTopic(uid), BuildStateJson(
            on: st.IsOn,
            briPercent: st.BrightnessPercent,
            colorMode: st.IsColorMode ? "rgb" : (st.ColorTempMireds.HasValue ? "color_temp" : null),
            color: st.Color,
            mireds: st.ColorTempMireds), retain: true);
    }

    // ---- Refresh (discovery + availability + last-known state) -----------------------------------

    private async Task RefreshAsync(CancellationToken ct)
    {
        var devices = await _api.GetDevicesAsync(ct);
        foreach (var d in devices)
        {
            if (string.IsNullOrWhiteSpace(d.Mac) || !d.Enabled) continue;
            if (!(d.Has("power") || d.Has("dim") || d.Has("color") || d.Has("colorTemp"))) continue;

            var uid = HaDiscovery.Uid(d.Mac);
            _uidToMac[uid] = d.Mac;

            await PublishAsync(_ha.LightDiscoveryTopic(uid), _ha.LightConfigPayload(d), retain: true);
            await PublishAsync(_ha.LightAvailabilityTopic(uid), d.Reachable ? "online" : "offline", retain: true);

            if (d.State is { } api)
            {
                var mode = api.Color is not null ? "rgb" : (api.ColorTemp.HasValue ? "color_temp" : null);
                if (mode is not null) _colorMode[uid] = mode == "rgb";
                await PublishAsync(_ha.LightStateTopic(uid), BuildStateJson(
                    on: api.On ?? false,
                    briPercent: mode == "rgb" ? (api.ColorBrightness ?? api.Brightness) : api.Brightness,
                    colorMode: mode,
                    color: api.Color,
                    mireds: api.ColorTemp), retain: true);
            }
        }

        var scenes = await _api.GetScenesAsync(ct);
        foreach (var sc in scenes)
            await PublishAsync(_ha.SceneDiscoveryTopic(sc.Id), _ha.SceneConfigPayload(sc), retain: true);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string BuildStateJson(bool on, int? briPercent, string? colorMode, ApiRgb? color, int? mireds)
    {
        var state = new Dictionary<string, object?> { ["state"] = on ? "ON" : "OFF" };
        if (briPercent is { } pct)
            state["brightness"] = Math.Clamp((int)Math.Round(pct / 100.0 * 255.0), 0, 255);
        if (colorMode is not null)
        {
            state["color_mode"] = colorMode;
            if (colorMode == "rgb" && color is not null)
                state["color"] = new { r = color.R, g = color.G, b = color.B };
            else if (colorMode == "color_temp" && mireds.HasValue)
                state["color_temp"] = mireds.Value;
        }
        return JsonSerializer.Serialize(state, Json.Opts);
    }

    private Task PublishAsync(string topic, string payload, bool retain)
    {
        if (!_mqtt.IsConnected) return Task.CompletedTask;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        return _mqtt.PublishAsync(msg, _ct);
    }
}
