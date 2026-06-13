namespace AwoxController.Core.Models;

/// <summary>Which AwoX BLE command dialect a bulb speaks (they share login, differ on commands).</summary>
public enum LightProtocol
{
    /// <summary>Older AwoX SmartLight / EGLO connect bulbs (.ble.tlmesh): 20-byte CTR command frame.</summary>
    Tlmesh = 0,

    /// <summary>Newer EGLO-ZM / connect-z bulbs (.ble.zigbee, FW 3.x): 17-byte AES-ECB command frame.</summary>
    Zigbee = 1,
}

/// <summary>
/// A mesh network's shared credentials. An AwoX account can have several (e.g. one "mesh" network
/// for older tlmesh bulbs and one "zigbee" network for newer ones); each bulb belongs to exactly
/// one. The login handshake is identical across protocols — only the command frame differs.
/// </summary>
public sealed class MeshNetwork
{
    public int Id { get; set; }

    /// <summary>The AwoX cloud "service" tag this network came from: "mesh" or "zigbee".</summary>
    public string Service { get; set; } = "";

    /// <summary>Mesh name (cloud client_id); a bulb's advertised BLE GAP name equals this.</summary>
    public string MeshName { get; set; } = "";

    /// <summary>Mesh password (cloud access_token).</summary>
    public string MeshPassword { get; set; } = "";

    /// <summary>Long-term mesh key (cloud refresh_token); reserved for future re-keying.</summary>
    public string MeshKey { get; set; } = "";

    public List<LampDevice> Lamps { get; set; } = new();
}

/// <summary>A single controllable bulb, persisted so it can be added/named/removed at runtime.</summary>
public sealed class LampDevice
{
    public int Id { get; set; }

    /// <summary>Friendly name, unique, used as the API device id (e.g. "Wohnzimmer Esstisch").</summary>
    public string Name { get; set; } = "";

    /// <summary>BLE MAC address "AA:BB:CC:DD:EE:FF" (unique).</summary>
    public string Mac { get; set; } = "";

    /// <summary>The bulb's mesh id / address used as the command destination (0..65535).</summary>
    public int MeshId { get; set; }

    /// <summary>Command dialect this bulb needs.</summary>
    public LightProtocol Protocol { get; set; }

    public string Model { get; set; } = "AwoX SmartLight";

    /// <summary>
    /// Optional free-text device category for the UI (e.g. "Smart Bulb", "Rovito Z", "Fernbedienung").
    /// Null until the user classifies the device. Remotes/switches are not driveable but kept for context.
    /// </summary>
    public string? DeviceType { get; set; }

    /// <summary>Optional room/group label for the UI.</summary>
    public string? Room { get; set; }

    /// <summary>
    /// True when the bulb has physically separate white and RGB channels (e.g. Rovito Z), driven
    /// independently — white brightness (0xF1) AND colour brightness (0xF2) at the same time. Such a
    /// device advertises the extra "colorBrightness" capability. Single-channel bulbs leave this false.
    /// </summary>
    public bool SeparateWhiteColor { get; set; }

    /// <summary>
    /// Last <em>commanded</em> state as JSON ({on,brightness,colorBrightness,color,colorTemp}), so the
    /// UI can show what was last sent (these bulbs don't report their real state over BLE). Updated
    /// after each successful command; null until the first command. Best-effort, not the physical truth.
    /// </summary>
    public string? LastState { get; set; }

    /// <summary>When false the bulb is hidden from control/listing without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Owning mesh (credentials). Null until assigned (e.g. a manually-added bulb before import).</summary>
    public int? MeshNetworkId { get; set; }
    public MeshNetwork? Mesh { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
