using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using AwoxController.Ble;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AwoxController.Ble.Tests;

/// <summary>
/// The Var1 relay-verify decision table: relay through a held same-mesh node vs. connect directly, and —
/// whenever a predicate is present — ALWAYS confirm via the target's advert. Confirmation runs in the
/// background of the real worker; the tests drive <see cref="RelayCoordinator.ExecuteAwaitingVerifyAsync"/>
/// (which awaits it) so routing, verification (WaitCalls), learning, and the direct retry (RequeueDirect
/// instead of an inline reconnect) are deterministic.
/// </summary>
public class RelayCoordinatorTests
{
    private const string Mesh = "mesh", Pw = "pw";
    private const string Host = "a4:c1:38:00:00:01";   // held gateway H
    private const string Target = "a4:c1:38:00:00:02";  // command target T

    // --- send-as-is cases (no relay decision, no verification) ---

    [Fact]
    public async Task FlagOff_SendsDirectToCommandGateway_NoVerify()
    {
        var (coord, sends, conn, settings, stream, retry) = Build();
        settings.RelayEnabled = false;
        conn.Hold(Host); // a session IS held, but the flag is off

        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);

        Assert.Equal(new[] { Target }, sends); // sent via the command's own gateway, no relay
        Assert.Equal(0, stream.WaitCalls);     // feature off → no advert verification
        Assert.Equal(0, retry.Count);
    }

    [Fact]
    public async Task NoPredicate_SendsDirect_NoVerify()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        conn.Hold(Host);
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: null), retry, default);
        Assert.Equal(new[] { Target }, sends);
        Assert.Equal(0, stream.WaitCalls); // nothing to confirm against
    }

    // --- direct-but-verified cases (no usable relay, but a predicate ⇒ still confirm via advert) ---

    [Fact]
    public async Task NoSessionHeld_SendsDirect_AndVerifies()
    {
        var (coord, sends, conn, _, stream, retry) = Build(); // nothing held
        stream.Result = true;
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);
        Assert.Equal(new[] { Target }, sends);
        Assert.Equal(1, stream.WaitCalls); // confirmed via the target's own advert
        Assert.Equal(0, retry.Count);      // direct, not relay → never retried
    }

    [Fact]
    public async Task DirectSend_Unconfirmed_DoesNotRetry()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        stream.Result = false; // no advert confirm
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);
        Assert.Equal(new[] { Target }, sends);
        Assert.Equal(0, retry.Count); // a direct send has nowhere to fall back to — no requeue
    }

    [Fact]
    public async Task HeldOnDifferentMesh_SendsDirect_AndVerifies()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        conn.Hold(Host, mesh: "other-mesh");
        stream.Result = true;
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);
        Assert.Equal(new[] { Target }, sends);
        Assert.Equal(1, stream.WaitCalls);
    }

    [Fact]
    public async Task HeldNodeIsTarget_SendsDirect_AndVerifies()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        conn.Hold(Target); // the held node already IS the target — no relay, but still confirm
        stream.Result = true;
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);
        Assert.Equal(new[] { Target }, sends);
        Assert.Equal(1, stream.WaitCalls);
    }

    // --- relay + verify cases ---

    [Fact]
    public async Task UnknownPair_Confirmed_RelaysViaHost_Learns_ButStillVerifiesNextTime()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        conn.Hold(Host);
        stream.Result = true; // advert confirms

        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);

        Assert.Equal(new[] { Host }, sends);  // relayed through the held node
        Assert.Equal(1, stream.WaitCalls);    // verification happened
        Assert.Equal(0, retry.Count);

        // A second command to the same (now-reachable) pair STILL verifies — we never trust a learned
        // verdict in place of a fresh advert. It still relays via the host (not marked unreachable).
        sends.Clear();
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);
        Assert.Equal(new[] { Host }, sends);
        Assert.Equal(2, stream.WaitCalls);    // re-verified, not short-circuited
    }

    [Fact]
    public async Task UnknownPair_Timeout_LearnsUnreachable_RequeuesDirect_ThenRoutesDirect()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        conn.Hold(Host);
        stream.Result = false; // advert never confirms

        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);

        Assert.Equal(new[] { Host }, sends); // relayed via host (verified); the fallback is a REQUEUE, not an inline send
        Assert.Equal(1, stream.WaitCalls);
        Assert.Equal(1, retry.Count);        // asked the queue to re-send directly

        // The pair is now learned unreachable → a fresh command skips the relay and goes straight to the
        // target. A direct send has no relay to fall back from, so it never requeues again.
        sends.Clear();
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn), retry, default);
        Assert.Equal(new[] { Target }, sends);
        Assert.Equal(2, stream.WaitCalls);
        Assert.Equal(1, retry.Count);        // unchanged — direct sends don't requeue
    }

    [Fact]
    public async Task TargetAlreadyInExpectedState_StillVerifies_DoesNotLearn_AndNeverRetries()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        conn.Hold(Host);
        stream.Result = true; // the lamp advertises its (already-correct) state

        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn, wasInState: true), retry, default);

        Assert.Equal(new[] { Host }, sends);   // relayed via host
        Assert.Equal(1, stream.WaitCalls);     // verification STILL happened (always advert-sure)
        Assert.Equal(0, retry.Count);

        // Map stayed Unknown (we didn't learn from the no-op) → a real-change command still relays+verifies.
        sends.Clear();
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn, wasInState: false), retry, default);
        Assert.Equal(new[] { Host }, sends);
        Assert.Equal(2, stream.WaitCalls);
    }

    [Fact]
    public async Task RelayTimeout_WhenAlreadyInState_DoesNotRequeue()
    {
        var (coord, sends, conn, _, stream, retry) = Build();
        conn.Hold(Host);
        stream.Result = false; // no confirming advert in time

        // Already in the wanted state + relay unconfirmed → the state is fine, don't churn a retry.
        await coord.ExecuteAwaitingVerifyAsync(Cmd(gateway: Target, expected: s => s.IsOn, wasInState: true), retry, default);

        Assert.Equal(new[] { Host }, sends);   // relay only — no requeue
        Assert.Equal(1, stream.WaitCalls);
        Assert.Equal(0, retry.Count);
    }

    // --- helpers ---

    private BleCommand Cmd(string gateway, Func<LightState, bool>? expected, bool wasInState = false)
        => new(gateway, Target, Mesh, Pw, 0x0002, BleChannel.Power,
               (_, gw, _) => { _lastSends.Add(gw); return Task.CompletedTask; },
               expected, PowerOff: false, TargetWasInExpectedState: wasInState);

    private readonly List<string> _lastSends = new();

    private (RelayCoordinator, List<string> sends, FakeConn, FakeSettings, FakeStream, FakeRetry) Build()
    {
        _lastSends.Clear();
        var conn = new FakeConn();
        var stream = new FakeStream();
        var settings = new FakeSettings();
        var coord = new RelayCoordinator(conn, stream, settings,
            NullLogger<RelayCoordinator>.Instance, new RelayReachabilityMap());
        return (coord, _lastSends, conn, settings, stream, new FakeRetry());
    }

    private sealed class FakeRetry : IBleCommandRetrySink
    {
        public int Count;
        public BleCommand? Last;
        public void RequeueDirect(BleCommand cmd) { Count++; Last = cmd; }
    }

    private sealed class FakeConn : IAwoxBleConnection
    {
        private string _mesh = Mesh, _pw = Pw;
        public string? ConnectedGatewayMac { get; private set; }
        public void Hold(string gateway, string mesh = Mesh, string pw = Pw)
        { ConnectedGatewayMac = gateway; _mesh = mesh; _pw = pw; }

        public bool IsConnected => ConnectedGatewayMac is not null;
        public bool IsConnecting => false;
        public DateTimeOffset? LastActivityUtc => null;
        public event Action<byte[]>? StatusReceived { add { } remove { } }
        public bool IsConnectedToMesh(string meshName, string meshPassword)
            => IsConnected && meshName == _mesh && meshPassword == _pw;
        public Task<bool> EnsureConnectedAsync(CancellationToken ct = default) => Task.FromResult(IsConnected);
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandAsync(ushort d, byte c, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendZigbeeCommandToAsync(string g, ushort d, byte[] c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandToAsync(string g, string mn, string mp, ushort d, byte c, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendZigbeeCommandToAsync(string g, string mn, string mp, ushort d, byte[] c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandToAsync(string g, ushort d, byte c, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> ReadStatusAsync(string g, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<AwoxLoginTestResult> TryLoginAsync(string mac, string mn, string mp, CancellationToken ct = default)
            => Task.FromResult(AwoxLoginTestResult.Failed(mn, mp, "n/a"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStream : IBleAdvertStream
    {
        public bool Result;
        public int WaitCalls;
        public bool HasSubscribers => false;
        public void Publish(string mac, LightState state) { }
        public Task<bool> WatchUntilAsync(string mac, Func<LightState, bool> match, CancellationToken until)
        { WaitCalls++; return Task.FromResult(Result); }
        public IAsyncEnumerable<AdvertUpdate> Watch(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSettings : IAppSettings
    {
        public bool RelayEnabled = true;
        public string? GetString(string key, string? fallback = null) => fallback;
        public int GetInt(string key, int fallback) => fallback;
        public double GetDouble(string key, double fallback) => fallback;
        public bool GetBool(string key, bool fallback)
            => key == AppSettingKeys.BleRelayVerifyEnabled ? RelayEnabled : fallback;
        public Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAsync(string key, string value, string? description = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureDefaultAsync(string key, string value, string? description = null, CancellationToken ct = default) => Task.CompletedTask;
        public void Reload() { }
    }
}
