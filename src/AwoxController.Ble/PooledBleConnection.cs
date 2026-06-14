using AwoxController.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AwoxController.Ble;

/// <summary>
/// An <see cref="IAwoxBleConnection"/> that holds up to <c>ble.max_connections</c> real connections at
/// once — ONE per mesh — so commands for DIFFERENT meshes run on their own held session instead of the
/// single global connection disconnect+reconnecting on every mesh switch. The command queue drains
/// distinct meshes in parallel against this pool; within one mesh everything still serialises on that
/// mesh's single underlying connection (its own gate), which is correct — a mesh fans out via
/// relay/broadcast, so a second link into the same mesh would only add dongle churn.
///
/// Routing: calls that carry mesh credentials (the DB-routed control path) go to that mesh's connection,
/// created lazily and LRU-evicted past the cap. Credential-less calls (the legacy BleLightService path,
/// the idle-disconnect service, the advert scan's IsBusy check) act on the DEFAULT mesh from
/// <see cref="AwoxBleOptions"/> — so nothing outside the multi-mesh control path changes behaviour.
/// With the cap at 1 this collapses to exactly the old single-connection behaviour.
/// </summary>
public sealed class PooledBleConnection : IAwoxBleConnection
{
    private readonly Func<IAwoxBleConnection> _factory;
    private readonly IAppSettings _settings;
    private readonly ILogger<PooledBleConnection> _logger;
    private readonly string _defaultKey;

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byMesh = new(StringComparer.Ordinal);
    private long _tick; // monotonic LRU stamp

    public event Action<byte[]>? StatusReceived;

    public PooledBleConnection(
        Func<IAwoxBleConnection> factory, IOptions<AwoxBleOptions> options,
        IAppSettings settings, ILogger<PooledBleConnection> logger)
    {
        _factory = factory;
        _settings = settings;
        _logger = logger;
        _defaultKey = Key(options.Value.MeshName, options.Value.MeshPassword);
    }

    private sealed class Entry(string key, IAwoxBleConnection conn)
    {
        public string Key { get; } = key;
        public IAwoxBleConnection Conn { get; } = conn;
        public long LastUsed { get; set; }
    }

    private static string Key(string meshName, string meshPassword) => $"{meshName}\0{meshPassword}";

    private int MaxConnections =>
        Math.Max(1, _settings.GetInt(AppSettingKeys.BleMaxConnections, AppSettingKeys.BleMaxConnectionsDefault));

    /// <summary>Gets (or lazily creates) the connection for a mesh, evicting the LRU mesh past the cap.</summary>
    private IAwoxBleConnection For(string meshName, string meshPassword)
    {
        var key = Key(meshName, meshPassword);
        Entry? evict = null;
        IAwoxBleConnection result;

        lock (_lock)
        {
            if (_byMesh.TryGetValue(key, out var existing))
            {
                existing.LastUsed = ++_tick;
                return existing.Conn;
            }

            // Make room: while at the cap, pick the least-recently-used mesh to drop. Done under the lock
            // (cheap — just dictionary bookkeeping); the actual disconnect happens outside it below.
            while (_byMesh.Count >= MaxConnections && _byMesh.Count > 0)
            {
                var lru = _byMesh.Values.OrderBy(e => e.LastUsed).First();
                _byMesh.Remove(lru.Key);
                evict = lru;
                break; // cap is enforced one-at-a-time; a single add never needs >1 eviction
            }

            var conn = _factory();
            conn.StatusReceived += OnUnderlyingStatus; // forward every mesh's status pushes
            var entry = new Entry(key, conn) { LastUsed = ++_tick };
            _byMesh[key] = entry;
            result = conn;
        }

        if (evict is not null)
        {
            _logger.LogInformation("BLE pool: evicting LRU mesh session to stay within ble.max_connections={Max}.", MaxConnections);
            evict.Conn.StatusReceived -= OnUnderlyingStatus;
            _ = SafeDisconnectAsync(evict.Conn); // fire-and-forget; the new session connects on first send
        }
        return result;
    }

    /// <summary>The default-mesh connection, used by every credential-less call (legacy/advert paths).</summary>
    private IAwoxBleConnection Default()
    {
        lock (_lock)
        {
            if (_byMesh.TryGetValue(_defaultKey, out var e)) { e.LastUsed = ++_tick; return e.Conn; }
        }
        var (n, p) = SplitKey(_defaultKey);
        return For(n, p);
    }

    private static (string, string) SplitKey(string key)
    {
        var i = key.IndexOf('\0');
        return (key[..i], key[(i + 1)..]);
    }

    private IAwoxBleConnection[] Snapshot()
    {
        lock (_lock) return _byMesh.Values.Select(e => e.Conn).ToArray();
    }

    private void OnUnderlyingStatus(byte[] frame) => StatusReceived?.Invoke(frame);

    // ---- Aggregate state ------------------------------------------------------------------

    // A session is held if ANY mesh connection holds one.
    public bool IsConnected => Snapshot().Any(c => c.IsConnected);

    // The advert scan yields the radio while ANY connection is mid-connect (a discovery restart would
    // abort an in-flight LE connect on the shared adapter), so aggregate across the pool.
    public bool IsConnecting => Snapshot().Any(c => c.IsConnecting);

    public string? ConnectedGatewayMac => Default().ConnectedGatewayMac;

    public DateTimeOffset? LastActivityUtc
    {
        get
        {
            DateTimeOffset? max = null;
            foreach (var c in Snapshot())
                if (c.LastActivityUtc is { } t && (max is null || t > max)) max = t;
            return max;
        }
    }

    public bool IsConnectedToMesh(string meshName, string meshPassword)
    {
        lock (_lock)
            return _byMesh.TryGetValue(Key(meshName, meshPassword), out var e) && e.Conn.IsConnectedToMesh(meshName, meshPassword);
    }

    public string? ConnectedGatewayMacOnMesh(string meshName, string meshPassword)
    {
        lock (_lock)
            return _byMesh.TryGetValue(Key(meshName, meshPassword), out var e)
                ? e.Conn.ConnectedGatewayMacOnMesh(meshName, meshPassword)
                : null;
    }

    // ---- Routed operations ----------------------------------------------------------------

    public Task<bool> EnsureConnectedAsync(CancellationToken ct = default) => Default().EnsureConnectedAsync(ct);

    public Task SendCommandAsync(ushort destId, byte command, byte[] data, CancellationToken ct = default)
        => Default().SendCommandAsync(destId, command, data, ct);

    public Task SendCommandToAsync(string gatewayMac, ushort destId, byte command, byte[] data, CancellationToken ct = default)
        => Default().SendCommandToAsync(gatewayMac, destId, command, data, ct);

    public Task SendCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte command, byte[] data, CancellationToken ct = default)
        => For(meshName, meshPassword).SendCommandToAsync(gatewayMac, meshName, meshPassword, destId, command, data, ct);

    public Task SendZigbeeCommandToAsync(string gatewayMac, ushort destId, byte[] command, CancellationToken ct = default)
        => Default().SendZigbeeCommandToAsync(gatewayMac, destId, command, ct);

    public Task SendZigbeeCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte[] command, CancellationToken ct = default)
        => For(meshName, meshPassword).SendZigbeeCommandToAsync(gatewayMac, meshName, meshPassword, destId, command, ct);

    public Task<byte[]?> ReadStatusAsync(string gatewayMac, CancellationToken ct = default)
        => Default().ReadStatusAsync(gatewayMac, ct);

    public Task<AwoxLoginTestResult> TryLoginAsync(string mac, string meshName, string meshPassword, CancellationToken ct = default)
        // Diagnostic probe: connects, tests creds, tears down without storing — run it on the mesh's
        // connection so it doesn't disturb the default session.
        => For(meshName, meshPassword).TryLoginAsync(mac, meshName, meshPassword, ct);

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        // Idle-disconnect (and shutdown) drop EVERY held mesh session — each re-connects on its next send.
        foreach (var c in Snapshot())
            await SafeDisconnectAsync(c, ct);
    }

    private async Task SafeDisconnectAsync(IAwoxBleConnection conn, CancellationToken ct = default)
    {
        try { await conn.DisconnectAsync(ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "BLE pool: disconnect of a pooled session failed (ignored)."); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in Snapshot())
        {
            c.StatusReceived -= OnUnderlyingStatus;
            try { await c.DisposeAsync(); } catch { /* best effort */ }
        }
    }
}
