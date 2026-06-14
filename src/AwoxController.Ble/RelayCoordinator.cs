using AwoxController.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AwoxController.Ble;

/// <summary>
/// Var1 relay-verify (see <c>var1-relay-verify-design</c>). For each queued command it decides whether
/// to relay through an already-held SAME-mesh gateway or connect directly to the target, and — whenever
/// the command carries a predicate — ALWAYS confirms the result against the target's own live advert
/// (the user's rule: a change we can't observe landing is a change we can't claim happened). It learns
/// which (host → target) relays actually deliver, so a known-bad relay is skipped, but it never trusts
/// a learned verdict in place of a fresh advert. Behind the <c>ble.relay_verify_enabled</c> flag.
/// </summary>
public interface IRelayCoordinator
{
    /// <summary>
    /// Sends <paramref name="cmd"/> — relaying through a held same-mesh node or directly — and returns as
    /// soon as the WRITE is out. When the command carries a predicate, the advert confirmation (and the
    /// learning + any direct retry via <paramref name="retry"/>) runs in the BACKGROUND, so the caller
    /// isn't held up by the up-to-several-seconds verify. Throws only on an unrecoverable send failure.
    /// </summary>
    Task ExecuteAsync(BleCommand cmd, IBleCommandRetrySink retry, CancellationToken ct);
}

public sealed class RelayCoordinator : IRelayCoordinator
{
    private readonly IAwoxBleConnection _connection;
    private readonly IBleAdvertStream _adverts;
    private readonly IAppSettings _settings;
    private readonly ILogger<RelayCoordinator> _logger;
    private readonly RelayReachabilityMap _map;

    public RelayCoordinator(
        IAwoxBleConnection connection, IBleAdvertStream adverts,
        IAppSettings settings, ILogger<RelayCoordinator> logger, RelayReachabilityMap map)
    {
        _connection = connection;
        _adverts = adverts;
        _settings = settings;
        _logger = logger;
        _map = map;
    }

    public async Task ExecuteAsync(BleCommand cmd, IBleCommandRetrySink retry, CancellationToken ct)
    {
        var confirm = await SendAndStartVerifyAsync(cmd, retry, ct);
        if (confirm is not null)
            _ = SwallowAsync(confirm); // fire-and-forget: don't hold the queue worker on the verify
    }

    /// <summary>Test seam: same as <see cref="ExecuteAsync"/> but awaits the background verify so a test
    /// can assert the confirmation/learning/retry deterministically.</summary>
    public async Task ExecuteAwaitingVerifyAsync(BleCommand cmd, IBleCommandRetrySink retry, CancellationToken ct)
    {
        var confirm = await SendAndStartVerifyAsync(cmd, retry, ct);
        if (confirm is not null) await confirm;
    }

    /// <summary>
    /// Decides the route, subscribes to the target's advert, SENDS (awaited — this is the only part the
    /// caller blocks on), and returns the not-yet-awaited confirm+learn+retry task (null when there's
    /// nothing to verify). Keeping the send awaited but the verify deferred is what lets the worker stream
    /// commands back-to-back while confirmation catches up in the background.
    /// </summary>
    private async Task<Task?> SendAndStartVerifyAsync(BleCommand cmd, IBleCommandRetrySink retry, CancellationToken ct)
    {
        // Feature off, or no observable predicate (e.g. an explicit ?via pin) → send through the command's
        // own gateway as-is; there's nothing to confirm against.
        if (!_settings.GetBool(AppSettingKeys.BleRelayVerifyEnabled, AppSettingKeys.BleRelayVerifyEnabledDefault)
            || cmd.ExpectedState is null)
        {
            await cmd.Send(_connection, cmd.GatewayMac, ct);
            return null;
        }

        // The node (if any) already holding a session ON THIS COMMAND'S MESH — H, or null. Mesh-scoped so
        // it's correct with the per-mesh connection pool: we relay only through a node held on the same
        // mesh, never the (possibly different) mesh another pooled session is on.
        var host = _connection.ConnectedGatewayMacOnMesh(cmd.MeshName, cmd.MeshPassword);

        // Prefer relaying through an already-held SAME-mesh node (no reconnect) — unless it IS the target,
        // or this (H,T) relay is known-unreachable (then go straight to the target). Reachability WITHIN
        // the mesh is settled by the advert confirmation below, not assumed.
        var canRelay = host is not null
            && !SameMac(host, cmd.TargetMac)
            && _map.Get(host, cmd.TargetMac) != RelayReachabilityMap.Reachability.Unreachable;
        var gateway = canRelay ? host! : cmd.GatewayMac;

        var timeout = TimeSpan.FromMilliseconds(
            _settings.GetInt(AppSettingKeys.BleRelayVerifyTimeoutMs, AppSettingKeys.BleRelayVerifyTimeoutMsDefault));

        // Subscribe BEFORE the write so a fast confirming advert can't slip past us, then send. The confirm
        // window's clock starts only AFTER the send returns — otherwise a multi-second cold connect inside
        // Send would consume (or exhaust) the window before the command is even out. We await only the
        // send; the verify task is handed back to run in the background (ConfirmAsync disposes the CTS).
        var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var verify = _adverts.WatchUntilAsync(cmd.TargetMac, cmd.ExpectedState!, until.Token);
        await cmd.Send(_connection, gateway, ct);
        until.CancelAfter(timeout);
        return ConfirmAsync(cmd, gateway, viaRelay: canRelay, verify, until, timeout, retry, ct);
    }

    /// <summary>
    /// Awaits the target's confirming advert, learns relay reachability from it (confirmed ⇒ reachable,
    /// timeout ⇒ unreachable — only for relay attempts that expected a real change), and on an unconfirmed
    /// relay asks the queue to re-send directly. Runs in the background; never blocks the send path.
    /// </summary>
    private async Task ConfirmAsync(BleCommand cmd, string gateway, bool viaRelay,
        Task<bool> verify, CancellationTokenSource until, TimeSpan timeout, IBleCommandRetrySink retry, CancellationToken ct)
    {
        bool confirmed;
        using (until) // owns the linked CTS that bounds the watch; dispose once the verify has resolved
            confirmed = await verify;

        // Learn only from a relay attempt that expected a real change — a confirming advert for a state the
        // lamp was ALREADY in proves nothing about whether the relay reached it.
        var learnable = viaRelay && !cmd.TargetWasInExpectedState;
        if (learnable)
            _map.Learn(gateway, cmd.TargetMac, reachable: confirmed);

        var route = viaRelay ? $"relay via {gateway}" : $"direct via {gateway}";
        _logger.LogInformation("Verify {Target} ({Channel}) {Route}: {Outcome}{Learned}.",
            cmd.TargetMac, cmd.Channel, route,
            confirmed ? "CONFIRMED" : $"NO advert confirm (≤{timeout.TotalMilliseconds:0}ms)",
            learnable ? $" — learned {(confirmed ? "reachable" : "unreachable")}" : "");

        // An unconfirmed RELAY (where we expected a change) might not have reached the target — re-send it
        // directly. The queue drops the retry if a newer command for the lamp has since superseded it.
        if (!confirmed && viaRelay && !cmd.TargetWasInExpectedState)
            retry.RequeueDirect(cmd);
    }

    private async Task SwallowAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Background relay-verify failed."); }
    }

    private static bool SameMac(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);

    private static string Norm(string mac) => mac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
}
