#if WINDOWS
using System.Security.Cryptography;
using AwoxController.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace AwoxController.Ble;

/// <summary>
/// WinRT (Windows) implementation of <see cref="IAwoxBleConnection"/> for local debugging on the
/// dev machine's built-in Bluetooth. Uses a persistent <see cref="GattSession"/> with
/// <c>MaintainConnection = true</c> so Windows holds the BLE link open and doesn't revoke GATT
/// access between operations. Without this, WinRT frequently returns AccessDenied on
/// vendor-specific characteristics after a reconnect.
/// </summary>
public sealed class WindowsBleConnection : IAwoxBleConnection
{
    private readonly AwoxBleOptions _options;
    private readonly IAppSettings _settings;
    private readonly ILogger<WindowsBleConnection> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private GattDeviceService? _meshService;
    private GattCharacteristic? _pairChar;
    private GattCharacteristic? _commandChar;
    private GattCharacteristic? _statusChar;
    private byte[]? _sessionKey;
    private string _gatewayMac = "";
    private string _sessionMeshName = "";
    private string _sessionMeshPassword = "";
    private string? _lastError;
    private DateTimeOffset? _lastActivityUtc;

    public WindowsBleConnection(IOptions<AwoxBleOptions> options, IAppSettings settings, ILogger<WindowsBleConnection> logger)
    {
        _options = options.Value;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _sessionKey is not null;

    // Windows has no passive advert scan competing for the radio, so this is never observed.
    public bool IsConnecting => false;

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
        if (!await EnsureConnectedToAsync(explicitMac, meshName, meshPassword, ct))
            throw new InvalidOperationException(_lastError ?? "AwoX BLE backend is not connected.");

        var sessionKey = _sessionKey ?? throw new InvalidOperationException("No session key.");
        var commandChar = _commandChar ?? throw new InvalidOperationException("No command characteristic.");
        var packet = AwoxMeshProtocol.MakeZigbeeCommandPacket(sessionKey, destId, command);
        _logger.LogDebug("ZB CMD → gateway={Mac} dest=0x{Dest:X4} packet={Hex}", _gatewayMac, destId, Convert.ToHexString(packet));

        await _gate.WaitAsync(ct);
        try
        {
            var status = await commandChar.WriteValueAsync(ToBuffer(packet), GattWriteOption.WriteWithoutResponse);
            _logger.LogDebug("ZB CMD write result: {Status}", status);
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

        if (!OperatingSystem.IsWindows())
            return Fail("BLE requires the Windows target framework (net10.0-windows10.0.19041.0).");

        if (target is null)
            return Fail("No gateway MAC (set AwoxBle:GatewayMac, add a device with a Mac, or call a MAC endpoint).");

        // Reuse a held session only for the SAME gateway — two lamps on one mesh may be out of radio
        // range of each other, so a held node can't be assumed to relay to a different target. Drive each
        // lamp by connecting to it directly (explicit ?via is the opt-in for a known-good relay node).
        if (IsSession(target, name, pw)) return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (IsSession(target, name, pw)) return true;
            if (IsConnected) Reset(); // different gateway and/or mesh credentials — reconnect
            return await ConnectAndLoginAsync(target, name, pw);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsConnectedToMesh(string meshName, string meshPassword)
        => IsConnected
           && string.Equals(_sessionMeshName, meshName, StringComparison.Ordinal)
           && string.Equals(_sessionMeshPassword, meshPassword, StringComparison.Ordinal);

    public string? ConnectedGatewayMacOnMesh(string meshName, string meshPassword)
        => IsConnectedToMesh(meshName, meshPassword) ? _gatewayMac : null;

    private bool IsSession(string gatewayMac, string meshName, string meshPassword)
        => IsConnectedToMesh(meshName, meshPassword)
           && string.Equals(_gatewayMac, gatewayMac, StringComparison.OrdinalIgnoreCase);

    private async Task SendCoreAsync(string? explicitMac, string? meshName, string? meshPassword, ushort destId, byte command, byte[] data, CancellationToken ct)
    {
        if (!await EnsureConnectedToAsync(explicitMac, meshName, meshPassword, ct))
            throw new InvalidOperationException(_lastError ?? "AwoX BLE backend is not connected.");

        try
        {
            await WriteCommandAsync(destId, command, data, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BLE command write failed; reconnecting and retrying once.");
            Reset();
            if (!await EnsureConnectedToAsync(explicitMac, meshName, meshPassword, ct))
                throw new InvalidOperationException(_lastError ?? "AwoX BLE reconnect failed.");
            await WriteCommandAsync(destId, command, data, ct);
        }
    }

    private async Task WriteCommandAsync(ushort destId, byte command, byte[] data, CancellationToken ct)
    {
        var sessionKey = _sessionKey ?? throw new InvalidOperationException("No session key.");
        var commandChar = _commandChar ?? throw new InvalidOperationException("No command characteristic.");

        var seq = RandomNumberGenerator.GetBytes(3);
        var packet = AwoxMeshProtocol.MakeCommandPacket(sessionKey, _gatewayMac, destId, command, data, seq);
        _logger.LogDebug("CMD → gateway={Mac} dest={Dest} op=0x{Op:X2} packet={Hex}",
            _gatewayMac, destId, command, Convert.ToHexString(packet));

        await _gate.WaitAsync(ct);
        try
        {
            // Telink/AwoX bulbs process control packets as "Write Without Response" (Write Command).
            // With WriteWithResponse the bulb ACKs at ATT level but silently ignores the command —
            // matches the Pi/BlueZ path, which writes without response and actually controls the bulb.
            var status = await commandChar.WriteValueAsync(ToBuffer(packet), GattWriteOption.WriteWithoutResponse);
            _logger.LogDebug("CMD write result: {Status}", status);
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Command write returned {status}.");
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]?> ReadStatusAsync(string gatewayMac, CancellationToken ct = default)
    {
        if (!await EnsureConnectedToAsync(gatewayMac, null, null, ct))
            throw new InvalidOperationException(_lastError ?? "AwoX BLE backend is not connected.");

        var sessionKey = _sessionKey ?? throw new InvalidOperationException("No session key.");
        var statusChar = _statusChar ?? throw new InvalidOperationException("No status characteristic.");

        await _gate.WaitAsync(ct);
        byte[] raw;
        try
        {
            var readResult = await statusChar.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (readResult.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Reading status returned {readResult.Status}.");
            raw = ToArray(readResult.Value);
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

    public async Task<AwoxLoginTestResult> TryLoginAsync(string mac, string meshName, string meshPassword, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return AwoxLoginTestResult.Failed(meshName, meshPassword, "BLE requires the Windows target framework.");

        await _gate.WaitAsync(ct);
        try
        {
            Reset();
            await Task.Delay(500, ct); // let Windows release old BLE handles

            if (!await OpenDeviceAndGattAsync(mac))
                return AwoxLoginTestResult.Failed(meshName, meshPassword, _lastError ?? $"Could not connect to {mac}.");

            if (_pairChar is null || _statusChar is null)
                return AwoxLoginTestResult.Failed(meshName, meshPassword, $"Pair/status characteristic missing on {mac}.");

            var sessionRandom = RandomNumberGenerator.GetBytes(8);
            var pairPacket = AwoxMeshProtocol.MakePairPacket(meshName, meshPassword, sessionRandom);
            await _pairChar.WriteValueAsync(ToBuffer(pairPacket), GattWriteOption.WriteWithResponse);
            await _statusChar.WriteValueAsync(ToBuffer(new byte[] { 0x01 }), GattWriteOption.WriteWithResponse);

            var readResult = await _pairChar.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (readResult.Status != GattCommunicationStatus.Success)
                return AwoxLoginTestResult.Failed(meshName, meshPassword, $"Reading the pair reply failed ({readResult.Status}).");

            var result = AwoxLoginTestResult.FromReply(meshName, meshPassword, ToArray(readResult.Value));
            _logger.LogInformation("Login test {Mac} name='{Name}' pw='{Pw}': {Msg}", mac, meshName, meshPassword, result.Message);

            Reset(); // clean up after test — don't hold the connection
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens the BluetoothLEDevice, creates a persistent GattSession (MaintainConnection=true),
    /// discovers the mesh service + characteristics, and stores them all as fields. Subsequent
    /// operations reuse these cached references instead of re-querying GATT (which triggers
    /// AccessDenied on WinRT after a reconnect).
    /// </summary>
    private async Task<bool> OpenDeviceAndGattAsync(string mac)
    {
        _logger.LogInformation("Opening BLE device {Mac}...", mac);

        var addr = ParseMac(mac);
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
        if (device is null)
        {
            // Not in the Windows BLE cache yet — actively scan a few seconds to discover it, then
            // retry. So a command to a bulb the OS hasn't seen recently triggers a scan instead of
            // failing immediately. (BlueZ already does its own discovery on cache miss.)
            _logger.LogInformation("{Mac} not in BLE cache — scanning to discover it...", mac);
            if (await ScanForAddressAsync(addr, TimeSpan.FromSeconds(8)))
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);

            if (device is null)
                return Fail($"AwoX bulb {mac} not reachable (powered off or out of range).");
        }

        // Create a GattSession FIRST — MaintainConnection tells Windows to actively hold the
        // BLE link, which prevents the AccessDenied-on-characteristics race.
        GattSession? session = null;
        try
        {
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            session.MaintainConnection = true;
            _logger.LogDebug("GattSession created for {Mac}, MaintainConnection=true, MaxPduSize={Pdu}.",
                mac, session.MaxPduSize);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GattSession.FromDeviceIdAsync failed for {Mac} — proceeding without persistent session.", mac);
        }

        var access = await device.RequestAccessAsync();
        if (access != DeviceAccessStatus.Allowed)
        {
            session?.Dispose();
            device.Dispose();
            return Fail($"Windows denied access to {mac} ({access}). Remove the bulb from Windows " +
                "Bluetooth settings (do NOT pair it) and close the AwoX phone app, then retry.");
        }

        // Retry the service discovery up to 3 times — WinRT can be flaky on the first attempt
        // right after the GattSession is opened.
        GattDeviceServicesResult? servicesResult = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            servicesResult = await device.GetGattServicesForUuidAsync(
                new Guid(AwoxBleConstants.MeshServiceUuid), BluetoothCacheMode.Uncached);

            if (servicesResult.Status == GattCommunicationStatus.Success && servicesResult.Services.Count > 0)
                break;

            _logger.LogDebug("Service discovery attempt {N}: status={Status}, services={Count}. Retrying...",
                attempt, servicesResult.Status, servicesResult.Services.Count);

            if (attempt < 3)
                await Task.Delay(1000);
        }

        if (servicesResult!.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
        {
            session?.Dispose();
            device.Dispose();
            return Fail($"Mesh GATT service not found on {mac} (status {servicesResult.Status}). " +
                "If AccessDenied: remove the bulb from Windows Bluetooth settings (do NOT pair it), close the " +
                "AwoX phone app, toggle PC Bluetooth off/on, and make sure no other instance of this app is running.");
        }

        var service = servicesResult.Services[0];

        GattCharacteristicsResult? charResult = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            charResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

            if (charResult.Status == GattCommunicationStatus.Success)
                break;

            _logger.LogDebug("Characteristics discovery attempt {N}: status={Status}. Retrying...",
                attempt, charResult.Status);

            if (attempt < 3)
                await Task.Delay(1000);
        }

        if (charResult!.Status != GattCommunicationStatus.Success)
        {
            session?.Dispose();
            device.Dispose();
            return Fail($"Could not read mesh characteristics on {mac}: {charResult.Status}. " +
                "If AccessDenied: remove the bulb from Windows Bluetooth settings (do NOT pair it), close the " +
                "AwoX phone app, toggle PC Bluetooth off/on, and make sure no other instance of this app is running.");
        }

        var chars = charResult.Characteristics;
        _logger.LogInformation("Mesh characteristics on {Mac}: {Uuids}", mac,
            string.Join(", ", chars.Select(c => $"{c.Uuid} [{c.CharacteristicProperties}]")));

        var pairChar = chars.FirstOrDefault(c => c.Uuid == new Guid(AwoxMeshProtocol.PairCharUuid));
        var statusChar = chars.FirstOrDefault(c => c.Uuid == new Guid(AwoxMeshProtocol.StatusCharUuid));
        var commandChar = chars.FirstOrDefault(c => c.Uuid == new Guid(AwoxMeshProtocol.CommandCharUuid));
        if (pairChar is null || statusChar is null || commandChar is null)
        {
            session?.Dispose();
            device.Dispose();
            return Fail($"Mesh characteristics missing on {mac} (pair={pairChar is not null} " +
                $"status={statusChar is not null} command={commandChar is not null}).");
        }

        _device = device;
        _gattSession = session;
        _meshService = service;
        _pairChar = pairChar;
        _statusChar = statusChar;
        _commandChar = commandChar;
        _gatewayMac = mac;

        device.ConnectionStatusChanged += OnConnectionStatusChanged;

        return true;
    }

    /// <summary>
    /// Runs a short BLE advertisement scan until the bulb with <paramref name="addr"/> is seen (so
    /// WinRT caches it and <see cref="BluetoothLEDevice.FromBluetoothAddressAsync"/> resolves), or
    /// until <paramref name="timeout"/>. Returns true if the address was seen.
    /// </summary>
    private static Task<bool> ScanForAddressAsync(ulong addr, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        void OnReceived(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            if (e.BluetoothAddress == addr) tcs.TrySetResult(true);
        }

        watcher.Received += OnReceived;
        try { watcher.Start(); }
        catch { tcs.TrySetResult(false); }

        _ = Task.Delay(timeout).ContinueWith(_ => tcs.TrySetResult(false), TaskScheduler.Default);

        return tcs.Task.ContinueWith(t =>
        {
            watcher.Received -= OnReceived;
            try { watcher.Stop(); } catch { /* already stopped */ }
            return t.Result;
        }, TaskScheduler.Default);
    }

    private async Task<bool> ConnectAndLoginAsync(string mac, string meshName, string meshPassword)
    {
        if (!await OpenDeviceAndGattAsync(mac))
            return false;

        _logger.LogInformation("Starting AwoX login handshake on {Mac} (name='{Name}')...",
            mac, meshName);

        var sessionRandom = RandomNumberGenerator.GetBytes(8);
        var pairPacket = AwoxMeshProtocol.MakePairPacket(meshName, meshPassword, sessionRandom);

        var pairWriteStatus = await _pairChar!.WriteValueAsync(ToBuffer(pairPacket), GattWriteOption.WriteWithResponse);
        _logger.LogDebug("Pair write → {Status}", pairWriteStatus);
        if (pairWriteStatus != GattCommunicationStatus.Success)
        {
            Reset();
            return Fail($"Pair write failed ({pairWriteStatus}) on {mac}.");
        }

        // Read the device's random from the pair characteristic FIRST (reference sequence),
        // then subscribe + trigger status. Wrong order was: write 0x01 → read pair reply.
        var readResult = await _pairChar.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (readResult.Status != GattCommunicationStatus.Success)
        {
            Reset();
            return Fail($"Reading the pair reply failed ({readResult.Status}).");
        }

        var reply = ToArray(readResult.Value);
        _logger.LogInformation("Pair reply from {Mac}: {Hex} (first byte 0x{First:X2}, length {Len})",
            mac, Convert.ToHexString(reply), reply.Length > 0 ? reply[0] : 0, reply.Length);

        if (reply.Length < 9 || reply[0] != 0x0D)
        {
            Reset();
            return Fail(reply.Length >= 1 && reply[0] == 0x0E
                ? "AwoX login rejected (auth error 0x0E): check MeshName/MeshPassword."
                : $"AwoX login failed, unexpected pair reply: {Convert.ToHexString(reply)}.");
        }

        _lastError = null;
        _sessionKey = AwoxMeshProtocol.MakeSessionKey(
            meshName, meshPassword, sessionRandom, reply.AsSpan(1, 8).ToArray());
        _sessionMeshName = meshName;
        _sessionMeshPassword = meshPassword;
        _lastActivityUtc = DateTimeOffset.UtcNow;
        _logger.LogDebug("Session key derived: {Key}", Convert.ToHexString(_sessionKey));

        // KEY (RE'd from the app's btsnoop, isolated ACL conn 0x041): the app NEVER writes the status
        // CCCD — the Connect-Z pushes notifications on 1911 natively once polled, and writing the CCCD
        // is exactly what makes it drop the link (our old 0x0B + disconnect). So we only register the
        // ValueChanged handler (no CCCD write) and keep the link alive + status flowing with the app's
        // 5s flag-0x02 broadcast poll.
        // NO CCCD write — the Connect-Z rejects it (throws on WinRT) and drops the link. Instead we
        // poll (flag-0x02) every 5s AND READ the status char each time: 1911 is readable, so we get the
        // pushed state without a notify subscription (which is the whole reason the app holds its link).
        _statusChar.ValueChanged += OnStatusValueChanged; // harmless; fires only if the stack ever pushes

        _logger.LogInformation("Connected and logged in to AwoX mesh via gateway {Mac} (GattSession persistent).", mac);
        StartKeepAlive();
        return true;
    }

    private System.Threading.Timer? _keepAlive;

    /// <summary>Holds the link open (and refreshes status) with the app's 5s flag-0x02 broadcast poll.</summary>
    private void StartKeepAlive()
    {
        _keepAlive?.Dispose();
        // Interval is DB-tunable (app_settings: ble.poll_interval_seconds); read at (re)connect.
        var seconds = Math.Max(1, _settings.GetInt(AppSettingKeys.BlePollIntervalSeconds, AppSettingKeys.BlePollIntervalSecondsDefault));
        _keepAlive = new System.Threading.Timer(_ => _ = SafePollAsync(), null, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(seconds));
    }

    private void StopKeepAlive()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
    }

    private async Task SafePollAsync()
    {
        var sk = _sessionKey;
        var cc = _commandChar;
        if (sk is null || cc is null) return;
        try
        {
            var packet = AwoxMeshProtocol.MakeZigbeeStatusPoll(sk);
            await _gate.WaitAsync();
            try
            {
                await cc.WriteValueAsync(ToBuffer(packet), GattWriteOption.WriteWithoutResponse);
                _lastActivityUtc = DateTimeOffset.UtcNow;
            }
            finally { _gate.Release(); }

            // READ the status char (no notify/CCCD): give the bulb a moment, then read 1911.
            var sc = _statusChar;
            if (sc is null) return;
            await Task.Delay(150);
            await _gate.WaitAsync();
            GattReadResult rr;
            try { rr = await sc.ReadValueAsync(BluetoothCacheMode.Uncached); }
            finally { _gate.Release(); }

            if (rr.Status != GattCommunicationStatus.Success) return;
            var raw = ToArray(rr.Value);
            _logger.LogInformation("STATUS READ ← {Mac} raw={Hex} ({Len}B)", _gatewayMac, Convert.ToHexString(raw), raw.Length);
            if (raw.Length >= 17)
            {
                var block = AwoxMeshProtocol.DecryptZigbee(sk, raw.AsSpan(1, 16));
                _logger.LogInformation("STATUS read-decoded flag=0x{F:X2} block={B}", raw[0], Convert.ToHexString(block));
                StatusReceived?.Invoke(block);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Keepalive status poll/read failed.");
        }
    }

    private void OnStatusValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var raw = ToArray(args.CharacteristicValue);
        // Raw log so we can reverse-engineer the Connect-Z status frame (20-byte, flag 0x80/0x82).
        _logger.LogInformation("STATUS NOTIFY ← {Mac} raw={Hex} ({Len}B)", _gatewayMac, Convert.ToHexString(raw), raw.Length);

        var sessionKey = _sessionKey;
        if (sessionKey is null) return;

        var decrypted = AwoxMeshProtocol.DecryptPacket(sessionKey, _gatewayMac, raw);
        if (decrypted is not null)
        {
            _logger.LogInformation("STATUS tlmesh-decoded={Hex}", Convert.ToHexString(decrypted));
            StatusReceived?.Invoke(decrypted);
            return;
        }

        // Connect-Z status: `flag(0x80/0x82) || AES-ECB-enc(16) || srcMeshId(2) || 0x60`; enc at offset 1.
        if (raw.Length >= 17)
        {
            var block = AwoxMeshProtocol.DecryptZigbee(sessionKey, raw.AsSpan(1, 16));
            var src = raw.Length >= 20 ? $"{raw[17]:X2}{raw[18]:X2}" : "----";
            _logger.LogInformation("STATUS zigbee flag=0x{Flag:X2} src={Src} block={Hex}", raw[0], src, Convert.ToHexString(block));
            StatusReceived?.Invoke(block);
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _logger.LogWarning("AwoX gateway {Mac} disconnected; session cleared.", _gatewayMac);
            _sessionKey = null;
            StopKeepAlive();
        }
    }

    private bool Fail(string message)
    {
        _lastError = message;
        _logger.LogError("{Error}", message);
        return false;
    }

    private string? ResolveConfigGatewayMac()
    {
        if (!string.IsNullOrWhiteSpace(_options.GatewayMac))
            return _options.GatewayMac;
        return _options.Devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Mac))?.Mac;
    }

    /// <summary>
    /// Cleanly drops the held session (disposing the GattSession releases the WinRT MaintainConnection
    /// hold) so the AwoX app can reclaim the bulb. Serialised on the command gate.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_device is null && _sessionKey is null) return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_device is not null || _sessionKey is not null)
                _logger.LogInformation("Disconnecting AwoX gateway {Mac} (idle or manual).", _gatewayMac);
            Reset();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Reset()
    {
        StopKeepAlive();
        _sessionKey = null;
        _lastActivityUtc = null;
        _commandChar = null;
        _statusChar = null;
        _pairChar = null;
        _meshService?.Dispose();
        _meshService = null;
        _gattSession?.Dispose();
        _gattSession = null;
        _device?.Dispose();
        _device = null;
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- WinRT helpers --------------------------------------------------------------------

    internal static ulong ParseMac(string mac)
    {
        var clean = mac.Replace(":", "").Replace("-", "");
        ulong addr = 0;
        for (var i = 0; i < clean.Length; i += 2)
            addr = (addr << 8) | Convert.ToByte(clean.Substring(i, 2), 16);
        return addr;
    }

    private static IBuffer ToBuffer(byte[] data)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        return writer.DetachBuffer();
    }

    private static byte[] ToArray(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }
}
#endif
