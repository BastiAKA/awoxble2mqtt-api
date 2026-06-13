using System.Collections.Concurrent;

namespace AwoxController.Ble;

/// <summary>
/// Learned "can host H relay a command to target T?" map for Var1 relay-verify
/// (<c>var1-relay-verify-design</c>). An entry is written only after an advert either confirmed
/// (reachable) or timed out (unreachable) for a relayed command, so verification is paid once per pair.
/// Entries expire after a TTL so a moved lamp / changed furniture is re-verified rather than trusted
/// forever — and the TTL is <b>asymmetric</b>: a confirmed verdict is trusted for a long time (stable
/// topology), but an unreachable verdict expires quickly so a transient failure (mesh congestion, the
/// lamp briefly off, a too-tight confirm window) is re-probed instead of sticking until restart.
/// In-memory only (resets on restart — relay-verify simply re-learns). Thread-safe.
/// </summary>
public sealed class RelayReachabilityMap
{
    public enum Reachability { Unknown, Reachable, Unreachable }

    private readonly record struct Entry(bool Reachable, DateTime LearnedUtc);

    private readonly ConcurrentDictionary<(string Host, string Target), Entry> _map = new();
    private readonly Func<TimeSpan> _reachableTtl;
    private readonly Func<TimeSpan> _unreachableTtl;
    private readonly Func<DateTime> _now;

    /// <param name="reachableTtl">How long a CONFIRMED verdict is trusted before re-verifying (default 6h).
    /// A delegate so it can track a runtime-tunable app-setting.</param>
    /// <param name="unreachableTtl">How long an UNREACHABLE verdict is trusted before the pair is
    /// re-probed (default 2min) — short, because failed relays are usually transient.</param>
    /// <param name="now">Clock injection point for tests; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public RelayReachabilityMap(
        Func<TimeSpan>? reachableTtl = null, Func<TimeSpan>? unreachableTtl = null, Func<DateTime>? now = null)
    {
        _reachableTtl = reachableTtl ?? (() => TimeSpan.FromHours(6));
        _unreachableTtl = unreachableTtl ?? (() => TimeSpan.FromMinutes(2));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The current verdict for (host → target), or <see cref="Reachability.Unknown"/> when never
    /// learned or expired (an expired entry is evicted on read). Unreachable entries expire on their own
    /// short TTL so a transient failure is re-probed rather than trusted until restart.</summary>
    public Reachability Get(string host, string target)
    {
        var key = Key(host, target);
        if (!_map.TryGetValue(key, out var e))
            return Reachability.Unknown;
        var ttl = e.Reachable ? _reachableTtl() : _unreachableTtl();
        if (_now() - e.LearnedUtc > ttl)
        {
            _map.TryRemove(key, out _);
            return Reachability.Unknown;
        }
        return e.Reachable ? Reachability.Reachable : Reachability.Unreachable;
    }

    /// <summary>Records whether host could relay to target, stamped now.</summary>
    public void Learn(string host, string target, bool reachable)
        => _map[Key(host, target)] = new Entry(reachable, _now());

    private static (string, string) Key(string host, string target) => (Norm(host), Norm(target));

    private static string Norm(string mac)
        => mac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
}
