using System.Text.Json;

namespace AwoxController.MqttBridge;

/// <summary>
/// Builds Home Assistant MQTT-discovery payloads + the matching state/command/availability topics.
/// Lamps map to HA's "light" with <c>schema:"json"</c> (one command/state topic carries on/off,
/// brightness, rgb and colour-temp); scenes map to HA's "scene" (a one-shot apply). HA then re-exports
/// everything to Apple Home / Google / Alexa / SmartThings via a Matter bridge add-on.
/// </summary>
public sealed class HaDiscovery(MqttOptions mqtt)
{
    private readonly string _base = mqtt.BaseTopic.TrimEnd('/');
    private readonly string _prefix = mqtt.DiscoveryPrefix.TrimEnd('/');

    // A stable per-lamp id for topics + unique_id: MAC without separators, lower-cased.
    public static string Uid(string mac) => mac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();

    public string LightStateTopic(string uid) => $"{_base}/light/{uid}/state";
    public string LightCommandTopic(string uid) => $"{_base}/light/{uid}/set";
    public string LightAvailabilityTopic(string uid) => $"{_base}/light/{uid}/availability";
    public string LightDiscoveryTopic(string uid) => $"{_prefix}/light/{uid}/config";

    public string SceneCommandTopic(int id) => $"{_base}/scene/{id}/set";
    public string SceneDiscoveryTopic(int id) => $"{_prefix}/scene/awox_scene_{id}/config";
    public const string ScenePayloadOn = "APPLY";

    // The command-topic filters the worker subscribes to.
    public string LightCommandFilter => $"{_base}/light/+/set";
    public string SceneCommandFilter => $"{_base}/scene/+/set";

    /// <summary>Pulls the lamp uid out of an inbound "<base>/light/<uid>/set" topic; null if it doesn't match.</summary>
    public string? LightUidFromCommandTopic(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length == 4 && parts[0] == _base && parts[1] == "light" && parts[3] == "set" ? parts[2] : null;
    }

    /// <summary>Pulls the scene id out of an inbound "<base>/scene/<id>/set" topic; null if it doesn't match.</summary>
    public int? SceneIdFromCommandTopic(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length == 4 && parts[0] == _base && parts[1] == "scene" && parts[3] == "set"
            && int.TryParse(parts[2], out var id) ? id : null;
    }

    public string LightConfigPayload(ApiDevice d)
    {
        var uid = Uid(d.Mac!);

        // Colour modes from the lamp's capabilities. HA wants supported_color_modes to be mutually
        // consistent: rgb + color_temp is the normal "RGBW-ish" bulb. Fall back to brightness-only.
        var modes = new List<string>();
        if (d.Has("color")) modes.Add("rgb");
        if (d.Has("colorTemp")) modes.Add("color_temp");
        if (modes.Count == 0) modes.Add(d.Has("dim") ? "brightness" : "onoff");

        var cfg = new Dictionary<string, object?>
        {
            ["name"] = d.Name,
            ["unique_id"] = $"awox_{uid}",
            ["schema"] = "json",
            ["command_topic"] = LightCommandTopic(uid),
            ["state_topic"] = LightStateTopic(uid),
            ["availability_topic"] = LightAvailabilityTopic(uid),
            ["brightness"] = d.Has("dim"),
            ["supported_color_modes"] = modes,
            ["device"] = new Dictionary<string, object?>
            {
                ["identifiers"] = new[] { $"awox_{uid}" },
                ["name"] = d.Name,
                ["manufacturer"] = "AwoX",
                ["model"] = d.Model ?? d.Type,
                ["connections"] = new[] { new[] { "mac", d.Mac } },
            },
        };

        // color_temp uses mireds in the JSON schema; AwoX lamps run 153 (cold) .. 500 (warm).
        if (modes.Contains("color_temp"))
        {
            cfg["min_mireds"] = 153;
            cfg["max_mireds"] = 500;
        }
        // Let HA pre-file the entity into the lamp's room.
        if (!string.IsNullOrWhiteSpace(d.Room))
            cfg["suggested_area"] = d.Room;

        return JsonSerializer.Serialize(cfg, Json.Opts);
    }

    public string SceneConfigPayload(ApiScene s)
    {
        var cfg = new Dictionary<string, object?>
        {
            ["name"] = s.Name,
            ["unique_id"] = $"awox_scene_{s.Id}",
            ["command_topic"] = SceneCommandTopic(s.Id),
            ["payload_on"] = ScenePayloadOn,
            ["icon"] = "mdi:palette",
        };
        return JsonSerializer.Serialize(cfg, Json.Opts);
    }
}
