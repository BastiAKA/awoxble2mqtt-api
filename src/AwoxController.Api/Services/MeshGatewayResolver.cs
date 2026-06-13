using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;

namespace AwoxController.Api.Services;

/// <summary>
/// Decides which mesh node to (first-)connect to when driving a lamp, and owns the shared "is a lamp
/// reachable" judgement (cache freshness window). See <see cref="MeshGatewayResolver"/>.
/// </summary>
public interface IMeshGatewayResolver
{
    /// <summary>How recently a lamp must have been seen to count as reachable (adaptive — see impl).</summary>
    TimeSpan OnlineWindow { get; }

    /// <summary>True when the lamp was seen (advert/command) within <see cref="OnlineWindow"/>.</summary>
    bool IsReachable(LampDevice lamp);

    /// <summary>Resolves the gateway MAC to connect to for driving <paramref name="target"/>.</summary>
    Task<string> ResolveGatewayAsync(LampDevice target, CancellationToken ct = default);
}

/// <summary>
/// Picks which mesh node to (first-)connect to when driving a lamp: the target itself when it's
/// reachable, otherwise any reachable lamp on the same mesh (the command then relays to the target by
/// its mesh id). This stops a burst — or a scene — stalling on a long failed connect to an offline
/// target while the rest of the mesh is perfectly reachable. A held same-mesh session is reused before
/// this even matters (see <c>IAwoxBleConnection</c> allowMeshRelay); this only chooses the FIRST connect.
/// </summary>
public sealed class MeshGatewayResolver : IMeshGatewayResolver
{
    private readonly ILightStateCache _cache;
    private readonly IAppSettings _settings;

    public MeshGatewayResolver(ILightStateCache cache, IAppSettings settings)
    {
        _cache = cache;
        _settings = settings;
    }

    public TimeSpan OnlineWindow =>
        TimeSpan.FromSeconds(Math.Max(60, _settings.GetInt(
            AppSettingKeys.BleOfflineAfterSeconds, AppSettingKeys.BleOfflineAfterSecondsDefault)));

    public bool IsReachable(LampDevice lamp) => _cache.IsFresh(lamp.Mac, OnlineWindow);

    public Task<string> ResolveGatewayAsync(LampDevice target, CancellationToken ct = default)
        // Always connect DIRECTLY to the target lamp. Two lamps on one mesh may be out of radio range of
        // each other, so we must NOT assume any other "reachable" node can relay a command to this one —
        // driving the wrong node silently does nothing. Cross-node relay is opt-in only via ?via=<node>,
        // where the caller asserts that node reaches the target.
        => Task.FromResult(target.Mac);
}
