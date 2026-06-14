using AwoxController.Ble;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AwoxController.Ble.Tests;

/// <summary>
/// Locks the per-mesh connection pool (roadmap #3): one held session per mesh, credential-less calls on
/// the default mesh, reuse for the same mesh, and LRU eviction past <c>ble.max_connections</c>. This is
/// what lets commands for different meshes run concurrently without the single connection disconnect+
/// reconnecting on every mesh switch.
/// </summary>
public class PooledBleConnectionTests
{
    private const string DefMesh = "defaultMesh", DefPw = "defaultPw";

    private static PooledBleConnection Build(int maxConnections, out List<FakeConn> created)
    {
        var made = new List<FakeConn>();
        created = made;
        var opts = Options.Create(new AwoxBleOptions { MeshName = DefMesh, MeshPassword = DefPw });
        var settings = new FakeSettings(maxConnections);
        Func<IAwoxBleConnection> factory = () => { var c = new FakeConn(); made.Add(c); return c; };
        return new PooledBleConnection(factory, opts, settings, NullLogger<PooledBleConnection>.Instance);
    }

    [Fact]
    public async Task RoutesEachMeshToItsOwnConnection_AndReusesIt()
    {
        var pool = Build(maxConnections: 3, out var created);

        await pool.SendCommandToAsync("gwA", "meshA", "pwA", 1, 0xD0, [1]);
        await pool.SendCommandToAsync("gwB", "meshB", "pwB", 2, 0xD0, [1]);
        await pool.SendCommandToAsync("gwA", "meshA", "pwA", 3, 0xD0, [0]); // same mesh again → reuse

        Assert.Equal(2, created.Count); // one per distinct mesh, not three
        Assert.Equal(2, created[0].Sends); // meshA connection used twice
        Assert.Equal(1, created[1].Sends); // meshB once
    }

    [Fact]
    public async Task CredentialLessCalls_UseTheDefaultMesh()
    {
        var pool = Build(maxConnections: 2, out var created);

        await pool.SendCommandAsync(1, 0xD0, [1]);                              // credential-less → default mesh
        await pool.SendCommandToAsync("gw", DefMesh, DefPw, 2, 0xD0, [1]);      // explicit default creds → same conn

        Assert.Single(created);
        Assert.Equal(2, created[0].Sends);
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsedMesh_PastTheCap()
    {
        var pool = Build(maxConnections: 2, out var created);

        await pool.SendCommandToAsync("gwA", "meshA", "pwA", 1, 0xD0, [1]);
        await pool.SendCommandToAsync("gwB", "meshB", "pwB", 2, 0xD0, [1]);
        await pool.SendCommandToAsync("gwA", "meshA", "pwA", 3, 0xD0, [1]); // touch A → B is now LRU
        await pool.SendCommandToAsync("gwC", "meshC", "pwC", 4, 0xD0, [1]); // cap=2 → evicts B

        Assert.Equal(3, created.Count);
        Assert.True(created[1].Disconnected); // meshB (the LRU) was disconnected on eviction
        Assert.False(created[0].Disconnected); // meshA still held
        Assert.False(created[2].Disconnected); // meshC freshly held
        Assert.False(pool.IsConnectedToMesh("meshB", "pwB"));
    }

    private sealed class FakeConn : IAwoxBleConnection
    {
        public int Sends;
        public bool Disconnected;
        private bool _connected;

        public bool IsConnected => _connected && !Disconnected;
        public bool IsConnecting => false;
        public string? ConnectedGatewayMac { get; private set; }
        public DateTimeOffset? LastActivityUtc => null;
        public event Action<byte[]>? StatusReceived { add { } remove { } }
        public bool IsConnectedToMesh(string meshName, string meshPassword) => IsConnected;
        public string? ConnectedGatewayMacOnMesh(string meshName, string meshPassword) => IsConnected ? ConnectedGatewayMac : null;
        public Task<bool> EnsureConnectedAsync(CancellationToken ct = default) { _connected = true; return Task.FromResult(true); }
        public Task DisconnectAsync(CancellationToken ct = default) { Disconnected = true; return Task.CompletedTask; }
        private Task Mark(string gw) { _connected = true; Disconnected = false; ConnectedGatewayMac = gw; Sends++; return Task.CompletedTask; }
        public Task SendCommandAsync(ushort d, byte c, byte[] data, CancellationToken ct = default) => Mark("default");
        public Task SendZigbeeCommandToAsync(string g, ushort d, byte[] c, CancellationToken ct = default) => Mark(g);
        public Task SendCommandToAsync(string g, string mn, string mp, ushort d, byte c, byte[] data, CancellationToken ct = default) => Mark(g);
        public Task SendZigbeeCommandToAsync(string g, string mn, string mp, ushort d, byte[] c, CancellationToken ct = default) => Mark(g);
        public Task SendCommandToAsync(string g, ushort d, byte c, byte[] data, CancellationToken ct = default) => Mark(g);
        public Task<byte[]?> ReadStatusAsync(string g, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<AwoxLoginTestResult> TryLoginAsync(string mac, string mn, string mp, CancellationToken ct = default)
            => Task.FromResult(AwoxLoginTestResult.Failed(mn, mp, "n/a"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSettings(int maxConnections) : IAppSettings
    {
        public string? GetString(string key, string? fallback = null) => fallback;
        public int GetInt(string key, int fallback) => key == AppSettingKeys.BleMaxConnections ? maxConnections : fallback;
        public double GetDouble(string key, double fallback) => fallback;
        public bool GetBool(string key, bool fallback) => fallback;
        public Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAsync(string key, string value, string? description = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureDefaultAsync(string key, string value, string? description = null, CancellationToken ct = default) => Task.CompletedTask;
        public void Reload() { }
    }
}
