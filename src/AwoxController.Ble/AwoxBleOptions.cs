namespace AwoxController.Ble;

/// <summary>
/// Configuration for the AwoX BLE mesh backend, bound from the "AwoxBle" config section.
///
/// AwoX SmartLight bulbs form a Bluetooth mesh: you connect to one reachable bulb (the gateway)
/// and address any bulb by its mesh id, which the gateway relays. There is no auto-discovery of
/// friendly names as with Zigbee2MQTT, so devices are listed here explicitly.
/// </summary>
public sealed class AwoxBleOptions
{
    public const string SectionName = "AwoxBle";

    /// <summary>Master switch. When false (default), the backend stays dormant and registers no devices.</summary>
    public bool Enabled { get; set; }

    /// <summary>The mesh name set in the AwoX HomeControl app (max 16 bytes UTF-8).</summary>
    public string MeshName { get; set; } = "unpaired";

    /// <summary>The mesh password set in the AwoX HomeControl app (max 16 bytes UTF-8).</summary>
    public string MeshPassword { get; set; } = "1234";

    /// <summary>
    /// Optional MAC of the bulb to connect to as the mesh gateway, e.g. "A4:C1:38:00:00:00".
    /// If empty, the first reachable device from <see cref="Devices"/> is used.
    /// </summary>
    public string GatewayMac { get; set; } = "";

    /// <summary>
    /// Auto-disconnect the held BLE session after this many seconds of inactivity, so the AwoX phone
    /// app (or another controller) can reclaim the bulb. A clean disconnect leaves the bulb advertising,
    /// so the next command simply reconnects. 0 (or less) = never auto-disconnect — hold the link
    /// indefinitely for the lowest command latency.
    /// </summary>
    public int MaxIdleDisconnectSeconds { get; set; }

    /// <summary>
    /// Seconds to wait for the BLE link to report "Connected" during a connect (BlueZ). Default 15.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Seconds to wait for GATT "ServicesResolved" after connecting (BlueZ). On a weak/distant link
    /// the service discovery does many round-trips and can exceed the old hard 15s, surfacing as a
    /// connect that succeeds then times out. Default 30 — raise it for marginal bulbs.
    /// </summary>
    public int ServicesResolvedTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How many times to attempt the full connect + service-discovery + login sequence before giving
    /// up (BlueZ). Between attempts the link is cleanly dropped and a short pause is taken — gentle, not
    /// a churn loop (churned/aborted connects can make AwoX bulbs stop advertising). Default 2.
    /// </summary>
    public int ConnectMaxAttempts { get; set; } = 2;

    /// <summary>
    /// Hard timeout (seconds) for a single GATT read/write D-Bus round-trip while the command gate is
    /// held (BlueZ). If a bulb drops the link mid-operation, the BlueZ <c>ReadValueAsync</c>/
    /// <c>WriteValueAsync</c> can otherwise hang forever holding the gate, wedging the ENTIRE BLE layer
    /// (every later command blocks on the gate, no error logged). With this timeout the stuck operation
    /// fails cleanly and releases the gate. Default 8. Must comfortably exceed a healthy round-trip
    /// (~0.1–0.5s) but stay well under a user's patience.
    /// </summary>
    public int OperationTimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// Hard timeout (seconds) for ACQUIRING the command gate before an operation gives up (BlueZ). The
    /// gate serialises everything on the link and is held for the whole connect+login (which can take
    /// tens of seconds on a marginal bulb), so this must comfortably exceed a legitimate connect. Its
    /// job is a backstop: if a previous operation ever wedges while holding the gate, later commands fail
    /// cleanly with a "BLE busy" timeout instead of parking forever — which previously piled up waiters
    /// and timers until the thread pool starved and a core span at 100%. Default 90.
    /// </summary>
    public int GateAcquireTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Read live Connect-Z status from passive BLE advertisements (no connection/login). Default true.
    /// The newer ("Zigbee") bulbs broadcast power/brightness/colour in their manufacturer data, so a
    /// passive scan reads state without ever stealing the link from the AwoX app/hub. Linux/BlueZ only;
    /// on Windows status comes from the WinRT connection instead. See <see cref="BleAdvStatusService"/>.
    /// </summary>
    public bool StatusScanEnabled { get; set; } = true;

    /// <summary>The bulbs in the mesh, mapped to friendly names used as API ids.</summary>
    public List<AwoxBleDevice> Devices { get; set; } = new();
}

/// <summary>A single bulb in the AwoX BLE mesh.</summary>
public sealed class AwoxBleDevice
{
    /// <summary>Friendly name, used as the device id in the API (e.g. "living_room").</summary>
    public string Name { get; set; } = "";

    /// <summary>The bulb's BLE MAC address, "AA:BB:CC:DD:EE:FF".</summary>
    public string Mac { get; set; } = "";

    /// <summary>The bulb's mesh id. 0 broadcasts to the whole mesh.</summary>
    public ushort MeshId { get; set; }

    /// <summary>Optional model string for the device listing.</summary>
    public string Model { get; set; } = "AwoX SmartLight";
}
