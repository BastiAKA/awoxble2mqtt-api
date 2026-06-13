namespace AwoxController.Zigbee;

/// <summary>
/// Bound from the "Zigbee2Mqtt" section of appsettings.json. Defaults match a
/// stock Zigbee2MQTT + Mosquitto install on the same Raspberry Pi.
/// </summary>
public sealed class Zigbee2MqttOptions
{
    public const string SectionName = "Zigbee2Mqtt";

    /// <summary>
    /// Master switch. When false, no MQTT connection is attempted (no reconnect loop / log noise) —
    /// useful while there is no broker/Zigbee2MQTT yet and only the BLE backend is in use.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "awox-controller";

    /// <summary>Must match "mqtt.base_topic" in Zigbee2MQTT's configuration.yaml.</summary>
    public string BaseTopic { get; set; } = "zigbee2mqtt";

    public int ReconnectDelaySeconds { get; set; } = 5;
}
