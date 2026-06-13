namespace AwoxController.Ble;

/// <summary>
/// A logged-in connection to one AwoX mesh bulb (the gateway) that relays commands to the mesh.
/// Implemented per platform: BlueZ on Linux/the Pi, WinRT on Windows for local debugging. The
/// transport-independent crypto lives in <see cref="AwoxMeshProtocol"/> and is shared by both.
/// </summary>
public interface IAwoxBleConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// True ONLY while a connect/login handshake is in flight. The passive advert scan yields the radio
    /// during this window — restarting LE discovery mid-connect makes BlueZ abort it
    /// (le-connection-abort-by-local). It deliberately does NOT cover a fully-held connection: the BT500
    /// scans fine while a connection is held (proven on hardware), so the scan keeps running then and we
    /// get live status of every lamp even during control.
    /// </summary>
    bool IsConnecting { get; }

    /// <summary>The MAC of the currently-held gateway bulb, or null when no session is held.</summary>
    string? ConnectedGatewayMac { get; }

    /// <summary>
    /// True when a session is currently held on these exact mesh credentials (any gateway node — a mesh
    /// relays mesh-wide). The relay coordinator uses this to decide whether the held gateway can forward
    /// a command to a target on the SAME mesh (necessary precondition — a node can't relay across meshes).
    /// </summary>
    bool IsConnectedToMesh(string meshName, string meshPassword);

    /// <summary>
    /// UTC time of the last successful connect or command on the held session, or null when no
    /// session is held. The idle-disconnect service uses this to decide when to drop the link.
    /// </summary>
    DateTimeOffset? LastActivityUtc { get; }

    /// <summary>Raised with a decrypted status packet whenever the gateway notifies a state change.</summary>
    event Action<byte[]>? StatusReceived;

    /// <summary>Ensures a logged-in connection exists (to the configured gateway). Returns false when unavailable.</summary>
    Task<bool> EnsureConnectedAsync(CancellationToken ct = default);

    /// <summary>
    /// Cleanly drops the held session (if any) so the AwoX app or another controller can reclaim the
    /// bulb. A clean disconnect keeps the bulb advertising, so the next command just reconnects. No-op
    /// when nothing is connected.
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Sends an encrypted command to the mesh via the configured gateway, addressed by mesh id (0 = all).</summary>
    Task SendCommandAsync(ushort destId, byte command, byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Sends a newer "Connect-Z" (.ble.zigbee) command (17-byte AES-ECB frame) to <paramref name="destId"/>
    /// via gateway <paramref name="gatewayMac"/>. <paramref name="command"/> is the opcode+data from the
    /// AwoxMeshProtocol.Zigbee*Command helpers. Used for the newer EGLO-ZM bulbs (firmware 3.x).
    /// </summary>
    Task SendZigbeeCommandToAsync(string gatewayMac, ushort destId, byte[] command, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="SendCommandToAsync(string,ushort,byte,byte[],CancellationToken)"/> but with explicit
    /// mesh credentials, so bulbs on different meshes can be driven from one running instance. The
    /// connection re-logs-in only when the gateway or the credentials change, and a held session is
    /// reused only for the SAME gateway — two lamps on one mesh may be out of radio range of each other,
    /// so connect to <paramref name="gatewayMac"/> to drive it (or pass a known-good relay node as it).
    /// </summary>
    Task SendCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte command, byte[] data, CancellationToken ct = default);

    /// <summary>As the other Zigbee overload but with explicit mesh credentials (multi-mesh).</summary>
    Task SendZigbeeCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte[] command, CancellationToken ct = default);

    /// <summary>
    /// Actively READS the status characteristic of <paramref name="gatewayMac"/> (connecting if
    /// needed) and returns the decrypted status packet, or null if it couldn't be read/decrypted.
    /// Works without a notify subscription — needed because some Eglo firmwares reject the notify
    /// CCCD write. Also raises <see cref="StatusReceived"/> so cached state updates.
    /// </summary>
    Task<byte[]?> ReadStatusAsync(string gatewayMac, CancellationToken ct = default);

    /// <summary>
    /// Sends a command after connecting to <paramref name="gatewayMac"/> as the gateway (reconnecting
    /// if currently connected to a different bulb). Used for direct, MAC-addressed control before the
    /// friendly-name/device mapping exists. dest 0 reaches the whole mesh through that bulb.
    /// </summary>
    Task SendCommandToAsync(string gatewayMac, ushort destId, byte command, byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Diagnostic: performs ONLY the AwoX login handshake against <paramref name="mac"/> with the
    /// given credentials, reports whether the bulb accepted them (reply 0x0D) or rejected them
    /// (0x0E), and tears the connection down again without storing a session. Used to discover which
    /// MeshName/MeshPassword a bulb actually belongs to, without restarting the app per attempt.
    /// </summary>
    Task<AwoxLoginTestResult> TryLoginAsync(string mac, string meshName, string meshPassword, CancellationToken ct = default);
}

/// <summary>Result of a one-shot <see cref="IAwoxBleConnection.TryLoginAsync"/> credential probe.</summary>
public sealed record AwoxLoginTestResult(bool Ok, string MeshName, string MeshPassword, string? ReplyHex, string Message)
{
    /// <summary>Connection-level failure (not reachable / no mesh service) — credentials untested.</summary>
    public static AwoxLoginTestResult Failed(string meshName, string meshPassword, string message)
        => new(false, meshName, meshPassword, null, message);

    /// <summary>Interprets the first byte of the pair reply: 0x0D = accepted, 0x0E = rejected.</summary>
    public static AwoxLoginTestResult FromReply(string meshName, string meshPassword, byte[] reply)
    {
        var hex = Convert.ToHexString(reply);
        if (reply.Length >= 9 && reply[0] == 0x0D)
            return new(true, meshName, meshPassword, hex, "OK — login accepted (0x0D). These are the right credentials for this bulb.");
        if (reply.Length >= 1 && reply[0] == 0x0E)
            return new(false, meshName, meshPassword, hex, "Rejected (0x0E) — wrong MeshName/MeshPassword for this bulb.");
        return new(false, meshName, meshPassword, hex, $"Unexpected pair reply (length {reply.Length}); the bulb may not speak Telink mesh.");
    }
}

/// <summary>
/// Discovers nearby BLE devices and probes whether they speak the AwoX/Telink mesh protocol.
/// Implemented per platform (BlueZ / WinRT). Registered independently of whether the BLE backend
/// is enabled, because you scan to find the MACs that go into <c>AwoxBle:Devices</c>.
/// </summary>
public interface IAwoxBleScanner
{
    /// <summary>Scans for <paramref name="duration"/> and returns devices seen, strongest signal first.</summary>
    Task<IReadOnlyList<DiscoveredBleDevice>> ScanAsync(TimeSpan duration, CancellationToken ct = default);

    /// <summary>Connects to one device and checks if it exposes the AwoX mesh GATT table (no creds needed).</summary>
    Task<BleProbeResult> ProbeAsync(string mac, CancellationToken ct = default);

    /// <summary>
    /// Diagnostic: connects and dumps the FULL GATT table (every service + characteristic with its
    /// attribute handle, UUID and properties) as text. Used to map the AwoX app's btsnoop handles
    /// (0x0020/0x0024/0x0028/0x0030 …) to UUIDs so the app's connection setup can be replicated.
    /// </summary>
    Task<string> DumpGattAsync(string mac, CancellationToken ct = default);
}

/// <summary>A BLE device seen during a scan.</summary>
public sealed class DiscoveredBleDevice
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public short? Rssi { get; set; }
    public string[] ServiceUuids { get; set; } = [];

    /// <summary>True when the device advertises the Telink mesh service UUID (controllable by this API).</summary>
    public bool AdvertisesMeshService { get; set; }

    /// <summary>True when the device advertises a Bluetooth SIG mesh service (proxy/provisioning).</summary>
    public bool IsSigMesh { get; set; }

    /// <summary>Heuristic: name or advertised services suggest an AwoX/Eglo bulb.</summary>
    public bool LooksLikeAwox { get; set; }

    /// <summary>Whether the advertisement is connectable (null when unknown / from a known-device listing).</summary>
    public bool? Connectable { get; set; }

    /// <summary>Bluetooth SIG company identifiers seen in the manufacturer data.</summary>
    public int[] ManufacturerCompanyIds { get; set; } = [];

    /// <summary>How the device was found: "advertisement", "paired", or "connected".</summary>
    public string Source { get; set; } = "advertisement";

    /// <summary>Human-readable guess at what this device is and whether this API can control it.</summary>
    public string Classification { get; set; } = "";
}

/// <summary>Result of probing one device's GATT table for the AwoX mesh protocol.</summary>
public sealed class BleProbeResult
{
    public string Address { get; set; } = "";
    public bool Connected { get; set; }
    public bool HasMeshService { get; set; }
    public bool HasPairChar { get; set; }
    public bool HasCommandChar { get; set; }
    public bool HasStatusChar { get; set; }
    public string[] ServiceUuids { get; set; } = [];
    public string? Error { get; set; }

    /// <summary>True when the full Telink mesh GATT table is present — our protocol should work.</summary>
    public bool SpeaksAwoxMesh => HasMeshService && HasPairChar && HasCommandChar && HasStatusChar;
}

/// <summary>Shared BLE constants and classification helpers for AwoX bulbs.</summary>
internal static class AwoxBleConstants
{
    /// <summary>Telink/AwoX mesh GATT service holding the pair/command/status characteristics.</summary>
    public const string MeshServiceUuid = "00010203-0405-0607-0809-0a0b0c0d1910";

    /// <summary>Bluetooth SIG Mesh Provisioning service (unprovisioned node advertises this).</summary>
    public const string SigMeshProvisioningUuid = "00001827-0000-1000-8000-00805f9b34fb";

    /// <summary>Bluetooth SIG Mesh Proxy service (a provisioned node in proxy mode advertises this).</summary>
    public const string SigMeshProxyUuid = "00001828-0000-1000-8000-00805f9b34fb";

    public static bool AdvertisesMeshService(IEnumerable<string> uuids)
        => uuids.Any(u => string.Equals(u, MeshServiceUuid, StringComparison.OrdinalIgnoreCase));

    public static bool IsSigMesh(IEnumerable<string> uuids)
        => uuids.Any(u => string.Equals(u, SigMeshProvisioningUuid, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(u, SigMeshProxyUuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// MAC OUIs of the BLE chipsets AwoX/Eglo bulbs use. These bulbs do NOT advertise their mesh
    /// service (it is only visible in the GATT table after connecting), so the MAC prefix is the
    /// most reliable advertisement-time signal. "A4:C1:38" is Telink Semiconductor.
    /// </summary>
    public static readonly string[] AwoxOuiPrefixes = { "A4:C1:38" };

    public static bool HasAwoxOui(string? mac)
        => !string.IsNullOrEmpty(mac)
           && AwoxOuiPrefixes.Any(p => mac.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeAwox(string? name, IEnumerable<string> uuids)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var hint in new[] { "awox", "sml", "eglo", "smart", "spm", "rcu", "rovito" })
                if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return AdvertisesMeshService(uuids);
    }

    /// <summary>A one-line guess at the device type and whether this API can control it.</summary>
    public static string Classify(bool telinkMesh, bool sigMesh, bool awoxOui, bool looksLikeAwox)
    {
        if (telinkMesh) return "AwoX/Telink mesh — controllable by this API (run probe).";
        if (sigMesh) return "Bluetooth SIG mesh — not controllable by this API yet (different protocol).";
        if (awoxOui) return "Likely AwoX/Telink bulb (Telink MAC) — run probe to confirm the protocol.";
        if (looksLikeAwox) return "AwoX-like name — connect/probe to confirm the protocol.";
        return "Unknown / not an AwoX bulb.";
    }
}
