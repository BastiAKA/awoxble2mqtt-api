using System.Security.Cryptography;
using AwoxController.Core.Interfaces;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AwoxController.Ble;

/// <summary>
/// BlueZ (Linux/Raspberry Pi) implementation of <see cref="IAwoxBleConnection"/>. Owns a single
/// GATT connection to one AwoX mesh bulb (the gateway) and performs the AwoX login handshake.
/// Once logged in, command packets are relayed by the gateway to the whole mesh.
///
/// Only functional on Linux. On other platforms <see cref="EnsureConnectedAsync"/> returns false
/// without touching D-Bus, so the app still builds and runs (use the WinRT backend on Windows).
/// </summary>
public sealed class BlueZBleConnection : IAwoxBleConnection
{
    private readonly AwoxBleOptions _options;
    private readonly ILogger<BlueZBleConnection> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Device? _device;
    private GattCharacteristic? _commandChar;
    private GattCharacteristic? _statusChar;
    private byte[]? _sessionKey;
    private string _gatewayMac = "";
    private string _sessionMeshName = "";
    private string _sessionMeshPassword = "";
    private DateTimeOffset? _lastActivityUtc;
    private Timer? _keepAlive;
    private volatile bool _connecting; // a connect/login is in flight — advert scan must yield the radio
    private readonly IAppSettings _settings;

    public BlueZBleConnection(IOptions<AwoxBleOptions> options, IAppSettings settings, ILogger<BlueZBleConnection> logger)
    {
        _options = options.Value;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _sessionKey is not null;

    public bool IsConnecting => _connecting;

    public string? ConnectedGatewayMac => IsConnected ? _gatewayMac : null;

    public DateTimeOffset? LastActivityUtc => _lastActivityUtc;

    public event Action<byte[]>? StatusReceived;

    public Task<bool> EnsureConnectedAsync(CancellationToken ct = default) => EnsureConnectedToAsync(null, null, null, ct);

    public Task SendCommandAsync(ushort destId, byte command, byte[] data, CancellationToken ct = default)
        => SendCoreAsync(null, null, null, destId, command, data, ct);

    public Task SendCommandToAsync(string gatewayMac, ushort destId, byte command, byte[] data, CancellationToken ct = default)
        => SendCoreAsync(gatewayMac, null, null, destId, command, data, ct);

    public Task SendCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte command, byte[] data, CancellationToken ct = default)
        => SendCoreAsync(gatewayMac, meshName, meshPassword, destId, command, data, ct);

    public Task SendZigbeeCommandToAsync(string gatewayMac, ushort destId, byte[] command, CancellationToken ct = default)
        => SendZigbeeCoreAsync(gatewayMac, null, null, destId, command, ct);

    public Task SendZigbeeCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte[] command, CancellationToken ct = default)
        => SendZigbeeCoreAsync(gatewayMac, meshName, meshPassword, destId, command, ct);

    private async Task SendZigbeeCoreAsync(string? explicitMac, string? meshName, string? meshPassword, ushort destId, byte[] command, CancellationToken ct)
    {
        // No status-notify/CCCD at all: these bulbs abort the link on a CCCD write (even post-login on
        // BlueZ). Status is pulled via the keepalive read-poll instead. See ConnectAndLoginAsync.
        if (!await EnsureConnectedToAsync(explicitMac, meshName, meshPassword, ct))
            throw new InvalidOperationException("AwoX BLE backend is not connected.");

        try
        {
            await WriteZigbeeAsync(destId, command, ct);
        }
        catch (Exception ex)
        {
            // The held connection may have dropped (idle/offended). Reconnect once and retry.
            _logger.LogWarning(ex, "Zigbee command write failed; reconnecting and retrying once.");
            await ResetAsync();
            if (!await EnsureConnectedToAsync(explicitMac, meshName, meshPassword, ct))
                throw new InvalidOperationException("AwoX BLE reconnect failed.");
            await WriteZigbeeAsync(destId, command, ct);
        }
        // Live status arrives via the passive advert scan (BleAdvStatusService), not this connection.
    }

    /// <summary>Holds the link open + refreshes status with the app's 5s flag-0x02 broadcast poll.</summary>
    private void StartKeepAlive()
    {
        _keepAlive?.Dispose();
        // Interval is DB-tunable (app_settings: ble.poll_interval_seconds); read at (re)connect.
        var seconds = Math.Max(1, _settings.GetInt(AppSettingKeys.BlePollIntervalSeconds, AppSettingKeys.BlePollIntervalSecondsDefault));
        _keepAlive = new Timer(_ => _ = SafePollAsync(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(seconds));
    }

    private void StopKeepAlive()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
    }

    private async Task SafePollAsync()
    {
        var sk = _sessionKey;
        if (sk is null || _commandChar is null) return;
        try
        {
            // Write-only keepalive: the flag-0x02 broadcast poll keeps the gateway link warm between
            // commands (until idle-disconnect). We deliberately do NOT read the status char back here:
            // on these bulbs the status read returns a single 0x00 (push-only, gated behind a CCCD the
            // FW rejects), so it carried no state — yet that gate-held ReadValueAsync was the exact
            // operation that hung forever when the bulb dropped the link mid-read, wedging the whole
            // BLE layer. Live status now comes from the passive advert scan (BleAdvStatusService), so
            // the read is pure liability. WriteRawAsync is itself timeout-bounded (see WithTimeoutAsync).
            await WriteRawAsync(AwoxMeshProtocol.MakeZigbeeStatusPoll(sk), CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Keepalive poll failed."); }
    }

    /// <summary>
    /// Runs a BlueZ D-Bus GATT operation with a hard timeout so it can never hang forever while holding
    /// <see cref="_gate"/>. A dropped link can make <c>ReadValueAsync</c>/<c>WriteValueAsync</c> never
    /// complete; without this the gate would stay held and every later command would block on it (the
    /// "API hangs after a lamp disconnects" deadlock). On timeout we surface a clean error and the
    /// caller's <c>finally</c> releases the gate. The orphaned D-Bus task may linger harmlessly.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(Task<T> op, string what)
    {
        try
        {
            return await op.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.OperationTimeoutSeconds)));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"BLE {what} on {_gatewayMac} timed out (link likely dropped).");
        }
    }

    private Task WithTimeoutAsync(Task op, string what) => WithTimeoutAsync(WrapAsync(op), what);

    private static async Task<bool> WrapAsync(Task op) { await op; return true; }

    /// <summary>
    /// Acquires the command gate with a hard timeout. The gate serialises everything on the link and is
    /// held for the whole connect+login, so the timeout must exceed a legitimate connect (see
    /// <see cref="AwoxBleOptions.GateAcquireTimeoutSeconds"/>). Its job is a backstop: if a previous
    /// operation ever wedges while holding the gate, later callers fail fast with a clean "BLE busy"
    /// error instead of parking forever — which is what previously piled up waiters/timers until the
    /// thread pool starved and a core spun at 100%. Throws before entering the caller's try/finally, so
    /// the gate is never released by a caller that didn't acquire it.
    /// </summary>
    private async Task AcquireGateAsync(string what, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(5, _options.GateAcquireTimeoutSeconds));
        if (!await _gate.WaitAsync(timeout, ct))
            throw new TimeoutException(
                $"BLE busy: could not acquire the command gate for {what} within {timeout.TotalSeconds:0}s " +
                "(a previous operation is stuck holding it).");
    }

    /// <summary>Writes a pre-built packet to the command characteristic (used for the status poll).</summary>
    private async Task WriteRawAsync(byte[] packet, CancellationToken ct)
    {
        var commandChar = _commandChar ?? throw new InvalidOperationException("No command characteristic.");
        _logger.LogDebug("ZB POLL → gateway={Mac} packet={Hex}", _gatewayMac, Convert.ToHexString(packet));
        await AcquireGateAsync("keepalive poll", ct);
        try
        {
            await WithTimeoutAsync(commandChar.WriteValueAsync(packet, WriteNoResponse), "keepalive poll write");
            // Deliberately do NOT refresh _lastActivityUtc here: this is the keepalive status poll, not a
            // user command. Counting it as activity would defeat the idle-disconnect (it runs every 5s),
            // so the link would be held forever — perpetually polling and starving the advert scan of the
            // radio. Only real commands (WriteZigbeeAsync / SendCommand) refresh the idle timer.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteZigbeeAsync(ushort destId, byte[] command, CancellationToken ct)
    {
        var sessionKey = _sessionKey ?? throw new InvalidOperationException("No session key.");
        var commandChar = _commandChar ?? throw new InvalidOperationException("No command characteristic.");
        var packet = AwoxMeshProtocol.MakeZigbeeCommandPacket(sessionKey, destId, command);
        _logger.LogDebug("ZB CMD → gateway={Mac} dest=0x{Dest:X4} packet={Hex}", _gatewayMac, destId, Convert.ToHexString(packet));

        await AcquireGateAsync("zigbee command", ct);
        try
        {
            await WithTimeoutAsync(commandChar.WriteValueAsync(packet, WriteNoResponse), "zigbee command write");
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureConnectedToAsync(string? explicitMac, string? meshName, string? meshPassword, CancellationToken ct)
    {
        var target = explicitMac ?? ResolveConfigGatewayMac();
        var name = meshName ?? _options.MeshName;
        var pw = meshPassword ?? _options.MeshPassword;

        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("BlueZ BLE is only supported on Linux. Connection skipped.");
            return false;
        }

        if (target is null)
        {
            _logger.LogError("No gateway MAC (set AwoxBle:GatewayMac, add a device with a Mac, or call a MAC endpoint).");
            return false;
        }

        // Reuse a held session ONLY when it's the SAME gateway. Two lamps on one mesh may be out of radio
        // range of EACH OTHER, so a held node cannot be assumed to relay a command to a different target
        // — driving the wrong-but-same-mesh node silently does nothing. So each lamp is driven by
        // connecting to IT directly. Explicit ?via=<node> is the opt-in for a known-good relay path.
        if (IsSession(target, name, pw)) return true;

        await AcquireGateAsync("connect+login", ct);
        try
        {
            if (IsSession(target, name, pw)) return true;
            if (IsConnected) await ResetAsync(); // different gateway and/or mesh credentials — reconnect

            // Mark the link busy for the whole connect: the advert scan checks IsBusy and stops
            // restarting LE discovery, which is what was aborting our connect (le-connection-abort-by-local)
            // and forcing the slow retry on the first command to each lamp.
            _connecting = true;

            // Retry the WHOLE connect + service-discovery + login a few times: on a weak link the
            // connect can succeed but ServicesResolved then times out, and that's recoverable on a
            // fresh try. ConnectAndLoginAsync cleanly disconnects on each failure, so between attempts
            // the bulb is released; a short pause keeps this gentle (never a churn loop).
            var attempts = Math.Max(1, _options.ConnectMaxAttempts);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (await ConnectAndLoginAsync(target, name, pw, ct)) return true;
                if (attempt < attempts)
                {
                    _logger.LogInformation("Connect+resolve attempt {N}/{Max} to {Mac} failed; retrying after a short pause.", attempt, attempts, target);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            return false;
        }
        finally
        {
            _connecting = false;
            _gate.Release();
        }
    }

    /// <summary>True when a session is live on these mesh credentials (any gateway node — the mesh
    /// relays mesh-wide, so the specific connected node doesn't matter for reuse).</summary>
    public bool IsConnectedToMesh(string meshName, string meshPassword)
        => IsConnected
           && string.Equals(_sessionMeshName, meshName, StringComparison.Ordinal)
           && string.Equals(_sessionMeshPassword, meshPassword, StringComparison.Ordinal);

    public string? ConnectedGatewayMacOnMesh(string meshName, string meshPassword)
        => IsConnectedToMesh(meshName, meshPassword) ? _gatewayMac : null;

    /// <summary>True when the live session matches this specific gateway AND mesh credentials.</summary>
    private bool IsSession(string gatewayMac, string meshName, string meshPassword)
        => IsConnectedToMesh(meshName, meshPassword)
           && string.Equals(_gatewayMac, gatewayMac, StringComparison.OrdinalIgnoreCase);

    private async Task SendCoreAsync(string? explicitMac, string? meshName, string? meshPassword, ushort destId, byte command, byte[] data, CancellationToken ct)
    {
        // HOLD the connection across commands (the configuration that worked). EnsureConnectedTo
        // re-logs-in only when the gateway or the mesh credentials change, so both meshes are supported
        // live without restarting.
        if (!await EnsureConnectedToAsync(explicitMac, meshName, meshPassword, ct))
            throw new InvalidOperationException("AwoX BLE backend is not connected.");

        try
        {
            await WriteCommandAsync(destId, command, data, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BLE command write failed; reconnecting and retrying once.");
            await ResetAsync();
            if (!await EnsureConnectedToAsync(explicitMac, meshName, meshPassword, ct))
                throw new InvalidOperationException("AwoX BLE reconnect failed.");
            await WriteCommandAsync(destId, command, data, ct);
        }
    }

    private async Task WriteCommandAsync(ushort destId, byte command, byte[] data, CancellationToken ct)
    {
        var sessionKey = _sessionKey ?? throw new InvalidOperationException("No session key.");
        var commandChar = _commandChar ?? throw new InvalidOperationException("No command characteristic.");

        var seq = RandomNumberGenerator.GetBytes(3);
        var packet = AwoxMeshProtocol.MakeCommandPacket(sessionKey, _gatewayMac, destId, command, data, seq);

        await AcquireGateAsync("command", ct);
        try
        {
            await WithTimeoutAsync(commandChar.WriteValueAsync(packet, WriteNoResponse), "command write");
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]?> ReadStatusAsync(string gatewayMac, CancellationToken ct = default)
    {
        try
        {
            if (!await EnsureConnectedToAsync(gatewayMac, null, null, ct))
                throw new InvalidOperationException("AwoX BLE backend is not connected.");

            var sessionKey = _sessionKey ?? throw new InvalidOperationException("No session key.");
            var statusChar = _statusChar ?? throw new InvalidOperationException("No status characteristic.");

            await AcquireGateAsync("status read", ct);
            byte[] raw;
            try
            {
                raw = await WithTimeoutAsync(statusChar.ReadValueAsync(EmptyOptions), "status read");
            }
            finally
            {
                _gate.Release();
            }

            var decrypted = AwoxMeshProtocol.DecryptPacket(sessionKey, _gatewayMac, raw);
            if (decrypted is null)
            {
                _logger.LogWarning("Read status from {Mac} but could not decrypt it (raw {Hex}).", gatewayMac, Convert.ToHexString(raw));
                return null;
            }

            StatusReceived?.Invoke(decrypted);
            return decrypted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status read from {Mac} failed.", gatewayMac);
            return null;
        }
    }

    public async Task<AwoxLoginTestResult> TryLoginAsync(string mac, string meshName, string meshPassword, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return AwoxLoginTestResult.Failed(meshName, meshPassword, "BlueZ BLE is only supported on Linux.");

        await AcquireGateAsync("login-test", ct);
        try
        {
            await ResetAsync(); // free any existing handle so we can open the device cleanly

            using var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
            if (adapter is null)
                return AwoxLoginTestResult.Failed(meshName, meshPassword, "No Bluetooth adapter found.");

            using var device = await FindDeviceAsync(adapter, mac, ct);
            if (device is null)
                return AwoxLoginTestResult.Failed(meshName, meshPassword, $"Bulb {mac} not found by BlueZ (out of range or not scanned).");

            try
            {
                await device.ConnectAsync();
                await device.WaitForPropertyValueAsync("Connected", true, TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
                await device.WaitForPropertyValueAsync("ServicesResolved", true, TimeSpan.FromSeconds(_options.ServicesResolvedTimeoutSeconds));

                var service = await device.GetServiceAsync(AwoxBleConstants.MeshServiceUuid);
                if (service is null)
                    return AwoxLoginTestResult.Failed(meshName, meshPassword, $"Mesh GATT service not found on {mac} — this bulb may not speak Telink mesh.");

                var pairChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.PairCharUuid);
                var statusChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.StatusCharUuid);
                if (pairChar is null || statusChar is null)
                    return AwoxLoginTestResult.Failed(meshName, meshPassword, $"Pair/status characteristic missing on {mac} — not a Telink-mesh bulb.");

                var sessionRandom = RandomNumberGenerator.GetBytes(8);
                var pairPacket = AwoxMeshProtocol.MakePairPacket(meshName, meshPassword, sessionRandom);
                await WithTimeoutAsync(pairChar.WriteValueAsync(pairPacket, EmptyOptions), "login-test pair write");

                var reply = await WithTimeoutAsync(pairChar.ReadValueAsync(EmptyOptions), "login-test pair read");
                var result = AwoxLoginTestResult.FromReply(meshName, meshPassword, reply);
                _logger.LogInformation("Login test {Mac} name='{Name}' pw='{Pw}': {Msg}", mac, meshName, meshPassword, result.Message);
                return result;
            }
            finally
            {
                await SafeDisconnectAsync(device);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ConnectAndLoginAsync(string mac, string meshName, string meshPassword, CancellationToken ct)
    {
        // Adapter + device acquisition is INSIDE the try: the USB dongle re-enumerates (hci0 ⇄ hci1),
        // which makes BlueZ purge objects, so GetAdaptersAsync/GetDeviceAsync can throw a D-Bus
        // UnknownObject on a stale proxy. That must be a clean failure (return false), not an unhandled
        // throw — the caller's retry loop then re-runs this method, which re-acquires a FRESH adapter and
        // recovers. (Previously these ran outside the try and the UnknownObject escaped to the worker.)
        Adapter? adapter = null;
        Device? device = null;
        var retained = false; // set once `device` is stored as the live `_device` (then we must NOT dispose it here)
        try
        {
            adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
            if (adapter is null)
            {
                _logger.LogError("No Bluetooth adapter found.");
                return false;
            }

            device = await FindDeviceAsync(adapter, mac, ct);
            if (device is null)
            {
                _logger.LogWarning("AwoX bulb {Mac} not found by BlueZ (powered off or out of range).", mac);
                return false;
            }

            device = await ConnectOnceAsync(adapter, device, mac, ct);
            await device.WaitForPropertyValueAsync("ServicesResolved", true, TimeSpan.FromSeconds(_options.ServicesResolvedTimeoutSeconds));

            // ServicesResolved can flip to true a beat before BlueZ has published the vendor service
            // objects on D-Bus, so GetServiceAsync may briefly return null. Retry a few times and log
            // what's actually there on the first miss for diagnosis.
            IGattService1? service = null;
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                service = await device.GetServiceAsync(AwoxBleConstants.MeshServiceUuid);
                if (service is not null) break;

                if (attempt == 1)
                {
                    var services = await device.GetServicesAsync();
                    var uuids = new List<string>();
                    foreach (var s in services) uuids.Add(await s.GetUUIDAsync());
                    _logger.LogWarning("Mesh service {Service} not yet present on {Mac}; services so far: [{Uuids}]. Retrying…",
                        AwoxBleConstants.MeshServiceUuid, mac, string.Join(", ", uuids));
                }
                await Task.Delay(700);
            }

            if (service is null)
            {
                _logger.LogError("Mesh GATT service {Service} not found on {Mac}.", AwoxBleConstants.MeshServiceUuid, mac);
                await SafeDisconnectAsync(device);
                return false;
            }

            var pairChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.PairCharUuid);
            var statusChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.StatusCharUuid);
            var commandChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.CommandCharUuid);
            if (pairChar is null || statusChar is null || commandChar is null)
            {
                _logger.LogError("Mesh characteristics missing on {Mac} (pair/status/command).", mac);
                await SafeDisconnectAsync(device);
                return false;
            }

            // Diagnostic only: log the status characteristic flags. We never act on "notify" — see below.
            var flags = await statusChar.GetFlagsAsync();
            _logger.LogInformation("Lamp has status chars for mac {mac}: {flags}", mac, string.Join(", ", flags));

            // Login handshake: write the pair packet, then read the reply. This is the EXACT sequence
            // that successfully controlled a bulb. Do NOT write 0x01 to the status char here — when it
            // worked, that write never happened (it sat after StartNotify, which threw first), and
            // active status reads don't need it. Adding it back changed login behaviour.
            var sessionRandom = RandomNumberGenerator.GetBytes(8);
            var pairPacket = AwoxMeshProtocol.MakePairPacket(meshName, meshPassword, sessionRandom);
            await WithTimeoutAsync(pairChar.WriteValueAsync(pairPacket, EmptyOptions), "login pair write");

            var reply = await WithTimeoutAsync(pairChar.ReadValueAsync(EmptyOptions), "login pair read");
            if (reply.Length < 9 || reply[0] != 0x0D)
            {
                if (reply.Length >= 1 && reply[0] == 0x0E)
                    _logger.LogError("AwoX login rejected (auth error): check MeshName/MeshPassword.");
                else
                    _logger.LogError("AwoX login failed, unexpected pair reply: {Reply}.", Convert.ToHexString(reply));
                await SafeDisconnectAsync(device);
                return false;
            }

            _sessionKey = AwoxMeshProtocol.MakeSessionKey(
                meshName, meshPassword, sessionRandom, reply.AsSpan(1, 8).ToArray());
            _gatewayMac = mac;
            _sessionMeshName = meshName;
            _sessionMeshPassword = meshPassword;
            _lastActivityUtc = DateTimeOffset.UtcNow;

            // We deliberately do NOT subscribe for push status (no CCCD/notify write). These AwoX bulbs
            // (Connect-Z / Eglo / .ble.zigbee) abort the link on the CCCD write — on BlueZ the rejected
            // write drops the connection right after, forcing a flaky reconnect for every command. So
            // there is no notify path at all: status is pulled by the keepalive instead, where a GATT
            // *read* never touches the CCCD (poll + read every 5s).
            _logger.LogInformation("No CCCD subscribe on {Mac} — status pulled via 5s poll+read (no notify).", mac);

            device.Disconnected += OnDeviceDisconnectedAsync;

            _device = device;
            retained = true; // now owned by the live session; ResetAsync disposes it on teardown
            _statusChar = statusChar;
            _commandChar = commandChar;

            await StopDiscoveryQuietlyAsync(adapter); // connected — free the radio
            _logger.LogInformation("Connected and logged in to AwoX mesh via gateway {Mac}.", mac);
            StartKeepAlive(); // 5s poll holds the link + pulls status (notify path) or read-polls it (no-CCCD path)
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not connect/login to AwoX bulb {Mac} (powered off, out of range, or BlueZ object churn).", mac);
            _sessionKey = null;
            if (device is not null) await SafeDisconnectAsync(device);
            if (adapter is not null) await StopDiscoveryQuietlyAsync(adapter);
            // NOTE: no adapter power-cycle / RemoveDevice here. That was a Raspberry-Pi-3-onboard
            // workaround; on a proper USB controller (ASUS BT500) it does more harm than good — it
            // wedges the adapter and the next command fails with "operation already in progress".
            return false;
        }
        finally
        {
            // Both proxies are IDisposable and each holds a D-Bus PropertiesChanged watcher. The adapter
            // is never retained, so always dispose it. The device is disposed UNLESS it became the live
            // _device (then ResetAsync owns its disposal). Without this, every connect cycle leaked them.
            adapter?.Dispose();
            if (!retained) device?.Dispose();
        }
    }


    private async Task<Device?> FindDeviceAsync(Adapter adapter, string mac, CancellationToken ct)
    {
        var device = await adapter.GetDeviceAsync(mac);
        if (device is not null) return device;

        _logger.LogInformation("Bulb {Mac} not yet known; starting discovery.", mac);
        try { await adapter.StartDiscoveryAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "StartDiscovery threw (maybe already running)."); }

        // Leave discovery RUNNING through the connect: a good USB controller (BT500) connects fine
        // while scanning, and — crucially — BlueZ keeps the freshly-discovered device object alive
        // (stopping discovery + pausing lets BlueZ purge it, then Connect fails with UnknownObject).
        // Discovery is stopped after a successful login (or on failure) by the caller.
        for (var i = 0; i < 15 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(1000, ct);
            device = await adapter.GetDeviceAsync(mac);
            if (device is not null) break;
        }

        return device;
    }

    private async Task StopDiscoveryQuietlyAsync(Adapter adapter)
    {
        try
        {
            if (await adapter.GetDiscoveringAsync())
                await adapter.StopDiscoveryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StopDiscovery threw (ignored).");
        }
    }

    /// <summary>
    /// A gentle connect that mirrors what <c>bluetoothctl connect</c> does (which succeeds reliably on
    /// this Pi where our old path aborted). KEY INSIGHT: the LE connect aborts with
    /// <c>le-connection-abort-by-local</c> when discovery is still running — bluetoothctl has the scan
    /// OFF at connect time. So: stop discovery, let the controller settle, THEN connect.
    ///
    /// At most ONE retry: stopping discovery can let BlueZ purge a freshly-discovered (unbonded) device
    /// so its proxy goes stale (UnknownObject), and a local abort is often transient — re-acquire the
    /// device object and try once more. Never loop: these bulbs get "offended" by churned/aborted
    /// connects and stop advertising until a mains power cycle.
    /// </summary>
    private async Task<Device> ConnectOnceAsync(Adapter adapter, Device device, string mac, CancellationToken ct)
    {
        await StopDiscoveryQuietlyAsync(adapter);
        // Settle after stopping the scan before connecting (else le-connection-abort-by-local). Tunable
        // (ble.connect_settle_ms) — was 1.5s for the Pi-3 onboard radio; far shorter on the BT500 dongle.
        var settleMs = Math.Clamp(
            _settings.GetInt(AppSettingKeys.BleConnectSettleMs, AppSettingKeys.BleConnectSettleMsDefault), 0, 3000);
        if (settleMs > 0) await Task.Delay(settleMs, ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Bound the connect itself: WaitForPropertyValueAsync below has its own timeout, but it
                // only runs AFTER ConnectAsync returns — a ConnectAsync that hangs (wedged BlueZ) would
                // otherwise hold the command gate forever. WithTimeoutAsync caps it so the gate is freed.
                await WithTimeoutAsync(device.ConnectAsync(), "connect");
                await device.WaitForPropertyValueAsync("Connected", true, TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
                _logger.LogDebug("Connected to {Mac} (attempt {Attempt}).", mac, attempt);
                return device;
            }
            catch (Exception ex) when (attempt == 1 && IsTransientConnectError(ex))
            {
                _logger.LogDebug(ex, "Connect attempt 1 failed for {Mac}; re-acquiring device and retrying once.", mac);
                var fresh = await ReacquireDeviceAsync(adapter, mac, ct);
                if (fresh is not null && !ReferenceEquals(fresh, device))
                {
                    device.Dispose(); // the superseded proxy's watcher would otherwise leak
                    device = fresh;
                }
                await Task.Delay(800, ct);
            }
        }
    }

    private static bool IsTransientConnectError(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("le-connection-abort-by-local", StringComparison.OrdinalIgnoreCase)
            || m.Contains("UnknownObject", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Software caused connection abort", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Re-fetch the device proxy (briefly re-scanning if BlueZ purged it), then stop discovery
    /// again so the follow-up connect runs with the scan OFF.</summary>
    private async Task<Device?> ReacquireDeviceAsync(Adapter adapter, string mac, CancellationToken ct)
    {
        var device = await adapter.GetDeviceAsync(mac);
        if (device is null)
        {
            try { await adapter.StartDiscoveryAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Re-acquire StartDiscovery threw."); }
            for (var i = 0; i < 8 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, ct);
                device = await adapter.GetDeviceAsync(mac);
                if (device is not null) break;
            }
        }
        await StopDiscoveryQuietlyAsync(adapter);
        await Task.Delay(1200, ct);
        return device;
    }

    /// <summary>
    /// Recovers a wedged Pi-3 controller by power-cycling the adapter over D-Bus (no sudo needed).
    /// After an aborted LE connect the onboard controller often won't see/connect the bulb again
    /// until it's reset; this is the software equivalent of <c>hciconfig hci0 reset</c>.
    /// </summary>
    private async Task PowerCycleAdapterAsync(Adapter adapter)
    {
        try
        {
            await adapter.SetPoweredAsync(false);
            await Task.Delay(1500);
            await adapter.SetPoweredAsync(true);
            await Task.Delay(1500);
            _logger.LogInformation("Bluetooth adapter power-cycled to recover from a wedged connect.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adapter power-cycle failed.");
        }
    }

    private Task OnDeviceDisconnectedAsync(Device sender, BlueZEventArgs e)
    {
        _logger.LogWarning("AwoX gateway {Mac} disconnected; session cleared.", _gatewayMac);
        StopKeepAlive();
        _sessionKey = null;
        return Task.CompletedTask;
    }

    private string? ResolveConfigGatewayMac()
    {
        if (!string.IsNullOrWhiteSpace(_options.GatewayMac))
            return _options.GatewayMac;
        return _options.Devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Mac))?.Mac;
    }

    /// <summary>
    /// Cleanly drops the held session so the AwoX app can reclaim the bulb. A clean BlueZ
    /// <c>DisconnectAsync</c> leaves the bulb advertising (verified), so the next command reconnects
    /// on attempt 1. Serialised on the same gate as commands so it can't race a write.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_device is null && _sessionKey is null) return; // nothing held

        await AcquireGateAsync("disconnect", ct);
        try
        {
            if (_device is not null || _sessionKey is not null)
                _logger.LogInformation("Disconnecting AwoX gateway {Mac} (idle or manual).", _gatewayMac);
            await ResetAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        StopKeepAlive();
        _sessionKey = null;
        _lastActivityUtc = null;
        var device = _device;
        _device = null;
        _commandChar = null;
        _statusChar = null;
        if (device is not null)
        {
            // Drop the Disconnected subscription we added at login (ConnectAndLoginAsync) BEFORE letting
            // go of the proxy — otherwise the handler + the Device proxy's D-Bus watcher linger on every
            // connect/disconnect cycle (idle-disconnect runs every few seconds when active), leaking.
            device.Disconnected -= OnDeviceDisconnectedAsync;
            await SafeDisconnectAsync(device);
            device.Dispose(); // removes the PropertiesChanged watcher; does NOT touch the BLE link
        }
    }

    private async Task SafeDisconnectAsync(Device device)
    {
        try { await device.DisconnectAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Disconnect threw (ignored)."); }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }

    private static Dictionary<string, object> EmptyOptions => new();

    /// <summary>
    /// BlueZ write options for "Write Without Response" (ATT Write Command). AwoX/Telink bulbs only
    /// act on commands sent this way; a default write (Write Request, with response) is rejected by
    /// the firmware with ATT 0x0e. Matches the WinRT path's GattWriteOption.WriteWithoutResponse.
    /// </summary>
    private static Dictionary<string, object> WriteNoResponse => new() { ["type"] = "command" };
}
