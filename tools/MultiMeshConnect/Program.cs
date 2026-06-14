using System.Diagnostics;
using AwoxController.Ble;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Real-operation feasibility test for MULTI-MESH concurrent control (roadmap #3). The bare-connect probe
// (tools/multi_connect_test.py) proved the dongle holds 2 link-layer connections at once; this goes the
// full distance on the PRODUCTION path: two independent BlueZBleConnection instances, each connecting +
// performing the AwoX LOGIN to a DIFFERENT mesh, then sending a real command — all CONCURRENTLY. It
// answers what the pool redesign hinges on: do two logins + encrypted writes on the shared adapter
// coexist, and does the dongle stay stable.
//
// It drives a visible power ON (hold) then OFF so you can see both lamps react. Credentials are passed as
// args (like RawAttSpike) so no secret lands in the repo.
//
// Usage:
//   MultiMeshConnect <macA> <meshA> <passA> <c|z> <destIdHexA>  <macB> <meshB> <passB> <c|z> <destIdHexB>  [holdSeconds]
// Example (Badezimmer tlmesh + Spiegel 1 zigbee):
//   MultiMeshConnect a4:c1:38:20:29:91 <meshA> <passA> c 2991  a4:c1:38:90:98:b8 <meshB> <passB> z 1111  3
// Linux/BlueZ only. Safe to run with awox-api up (proven to coexist); if a connect aborts with
// le-connection-abort-by-local, the API's advert scan restarted discovery mid-connect — re-run.

if (args.Length < 10)
{
    Console.Error.WriteLine(
        "Usage: MultiMeshConnect <macA> <meshA> <passA> <c|z> <destIdHexA>  " +
        "<macB> <meshB> <passB> <c|z> <destIdHexB>  [holdSeconds]");
    return 2;
}

var hold = args.Length > 10 && int.TryParse(args[10], out var h) ? h : 3;
var lampA = Lamp.Parse(args, 0);
var lampB = Lamp.Parse(args, 5);

void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

// Each lamp gets its OWN connection instance — that's the whole point (one session per mesh). No shared
// static state in BlueZBleConnection, so two instances drive two meshes independently over one adapter.
IAwoxBleConnection Connect(Lamp l) =>
    new BlueZBleConnection(Options.Create(new AwoxBleOptions()), new DefaultSettings(),
        new ConsoleLogger<BlueZBleConnection>($"conn:{l.Mac[^5..]}"));

async Task<Result> DriveAsync(IAwoxBleConnection conn, Lamp l, bool on)
{
    var sw = Stopwatch.StartNew();
    try
    {
        if (l.Zigbee)
            await conn.SendZigbeeCommandToAsync(l.Mac, l.Mesh, l.Pass, l.Dest, AwoxMeshProtocol.ZigbeePowerCommand(on));
        else
            await conn.SendCommandToAsync(l.Mac, l.Mesh, l.Pass, l.Dest, AwoxMeshProtocol.CmdPower, AwoxMeshProtocol.PowerPayload(on));
        sw.Stop();
        Log($"  {l.Mac} power={(on ? "ON" : "OFF")} ok in {sw.ElapsedMilliseconds}ms  (connected={conn.IsConnected})");
        return new Result(true, conn.IsConnected, sw.ElapsedMilliseconds, null);
    }
    catch (Exception ex)
    {
        sw.Stop();
        Log($"  {l.Mac} power={(on ? "ON" : "OFF")} FAILED in {sw.ElapsedMilliseconds}ms: {ex.Message}");
        return new Result(false, conn.IsConnected, sw.ElapsedMilliseconds, ex.Message);
    }
}

Log($"A={lampA}  B={lampB}  hold={hold}s");
var connA = Connect(lampA);
var connB = Connect(lampB);
try
{
    Log("Concurrent login + power ON (both meshes at once)...");
    var on = await Task.WhenAll(DriveAsync(connA, lampA, true), DriveAsync(connB, lampB, true));

    // The decisive check: are BOTH sessions held at the same moment, right after concurrent logins?
    var bothUp = connA.IsConnected && connB.IsConnected;
    Log($"BOTH sessions held simultaneously: {bothUp}  (A={connA.IsConnected}, B={connB.IsConnected})");

    Log($"holding {hold}s...");
    await Task.Delay(TimeSpan.FromSeconds(hold));
    Log($"still held after {hold}s: A={connA.IsConnected}, B={connB.IsConnected}");

    Log("Concurrent power OFF...");
    await Task.WhenAll(DriveAsync(connA, lampA, false), DriveAsync(connB, lampB, false));

    var ok = on[0].Ok && on[1].Ok && bothUp;
    Log($"RESULT: {(ok ? "PASS" : "FAIL")} — two-mesh concurrent control "
        + (ok ? "works on the production path." : "did NOT fully succeed (see above)."));
    return ok ? 0 : 1;
}
finally
{
    await connA.DisconnectAsync();
    await connB.DisconnectAsync();
    Log("disconnected both.");
}

internal readonly record struct Result(bool Ok, bool Connected, long Ms, string? Error);

internal sealed record Lamp(string Mac, string Mesh, string Pass, bool Zigbee, ushort Dest)
{
    public static Lamp Parse(string[] a, int i) => new(
        a[i].ToLowerInvariant(), a[i + 1], a[i + 2],
        a[i + 3].Equals("z", StringComparison.OrdinalIgnoreCase),
        (ushort)Convert.ToInt32(a[i + 4], 16));

    public override string ToString() => $"{Mac}({(Zigbee ? "zigbee" : "tlmesh")},dest=0x{Dest:X4})";
}

// Minimal IAppSettings: the connection only reads poll-interval + connect-settle, both with sane defaults.
internal sealed class DefaultSettings : IAppSettings
{
    public string? GetString(string key, string? fallback = null) => fallback;
    public int GetInt(string key, int fallback) => fallback;
    public double GetDouble(string key, double fallback) => fallback;
    public bool GetBool(string key, bool fallback) => fallback;
    public Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetAsync(string key, string value, string? description = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureDefaultAsync(string key, string value, string? description = null, CancellationToken ct = default) => Task.CompletedTask;
    public void Reload() { }
}

// Tiny ILogger so the connection's diagnostics print, without pulling in a logging-provider package.
internal sealed class ConsoleLogger<T>(string tag) : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
    {
        if (!IsEnabled(level)) return;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {level.ToString()[..4].ToUpper()}: {fmt(state, ex)}"
            + (ex is null ? "" : $" — {ex.Message}"));
    }
    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}
