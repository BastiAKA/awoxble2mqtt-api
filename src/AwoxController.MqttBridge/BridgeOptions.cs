namespace AwoxController.MqttBridge;

/// <summary>Bound from the "Bridge" config section. Where the AwoxController API is and how to reach MQTT.</summary>
public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    /// <summary>Base URL of the AwoxController API (REST + the /hubs/lights SignalR hub).</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Optional API key (sent as X-Api-Key) when the API has Auth:ApiKey set; null/empty = open.</summary>
    public string? ApiKey { get; set; }

    /// <summary>How often to re-poll the API for the device/scene list + availability (new lamps, reachability).
    /// Live state changes come instantly over SignalR; this is only the slow backstop.</summary>
    public int RefreshIntervalSeconds { get; set; } = 60;

    public MqttOptions Mqtt { get; set; } = new();
}

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "awox-mqtt-bridge";

    /// <summary>Home Assistant MQTT discovery prefix (HA default "homeassistant").</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>Root topic for this bridge's state/command/availability topics.</summary>
    public string BaseTopic { get; set; } = "awox";

    public int ReconnectDelaySeconds { get; set; } = 5;
}
