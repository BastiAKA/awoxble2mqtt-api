using AwoxController.Ble;
using Xunit;

namespace AwoxController.Ble.Tests;

/// <summary>
/// The learned (host → target) relay map: unknown until learned, MAC-normalised, and TTL-expiring so a
/// moved lamp is re-verified rather than trusted forever.
/// </summary>
public class RelayReachabilityMapTests
{
    [Fact]
    public void Unknown_BeforeAnythingLearned()
    {
        var map = new RelayReachabilityMap();
        Assert.Equal(RelayReachabilityMap.Reachability.Unknown, map.Get("AA:BB", "CC:DD"));
    }

    [Fact]
    public void Learn_RoundTrips_ReachableAndUnreachable()
    {
        var map = new RelayReachabilityMap();
        map.Learn("AA:BB", "CC:DD", reachable: true);
        map.Learn("AA:BB", "EE:FF", reachable: false);

        Assert.Equal(RelayReachabilityMap.Reachability.Reachable, map.Get("AA:BB", "CC:DD"));
        Assert.Equal(RelayReachabilityMap.Reachability.Unreachable, map.Get("AA:BB", "EE:FF"));
    }

    [Fact]
    public void Key_IsMacNormalised_SeparatorAndCaseInsensitive()
    {
        var map = new RelayReachabilityMap();
        map.Learn("a4:c1:38:00:00:01", "a4:c1:38:00:00:02", reachable: true);

        // Same MACs, different separators/case → same entry.
        Assert.Equal(RelayReachabilityMap.Reachability.Reachable,
            map.Get("A4C138000001", "a4-c1-38-00-00-02"));
        Assert.Equal(RelayReachabilityMap.Reachability.Reachable,
            map.Get("A4:C1:38:00:00:01", "A4:C1:38:00:00:02"));
    }

    [Fact]
    public void Directional_HostTargetNotSymmetric()
    {
        var map = new RelayReachabilityMap();
        map.Learn("AA:BB", "CC:DD", reachable: true);
        Assert.Equal(RelayReachabilityMap.Reachability.Unknown, map.Get("CC:DD", "AA:BB"));
    }

    [Fact]
    public void Expires_AfterReachableTtl_AndIsReLearnable()
    {
        var now = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var map = new RelayReachabilityMap(
            reachableTtl: () => TimeSpan.FromHours(6),
            unreachableTtl: () => TimeSpan.FromMinutes(2),
            now: () => now);

        map.Learn("AA:BB", "CC:DD", reachable: true);
        Assert.Equal(RelayReachabilityMap.Reachability.Reachable, map.Get("AA:BB", "CC:DD"));

        now = now.AddHours(7); // past the reachable TTL
        Assert.Equal(RelayReachabilityMap.Reachability.Unknown, map.Get("AA:BB", "CC:DD"));

        map.Learn("AA:BB", "CC:DD", reachable: false); // re-learn at the new time
        Assert.Equal(RelayReachabilityMap.Reachability.Unreachable, map.Get("AA:BB", "CC:DD"));
    }

    [Fact]
    public void Unreachable_ExpiresFast_OnItsOwnShortTtl_SoTransientFailureIsReProbed()
    {
        var now = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var map = new RelayReachabilityMap(
            reachableTtl: () => TimeSpan.FromHours(6),
            unreachableTtl: () => TimeSpan.FromMinutes(2),
            now: () => now);

        map.Learn("AA:BB", "CC:DD", reachable: false);
        Assert.Equal(RelayReachabilityMap.Reachability.Unreachable, map.Get("AA:BB", "CC:DD"));

        now = now.AddMinutes(1); // still inside the short unreachable window
        Assert.Equal(RelayReachabilityMap.Reachability.Unreachable, map.Get("AA:BB", "CC:DD"));

        now = now.AddMinutes(2); // past the unreachable TTL — re-probe, not pinned until restart
        Assert.Equal(RelayReachabilityMap.Reachability.Unknown, map.Get("AA:BB", "CC:DD"));
    }

    [Fact]
    public void Reachable_StillTrusted_WhilePastTheShortUnreachableWindow()
    {
        var now = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var map = new RelayReachabilityMap(
            reachableTtl: () => TimeSpan.FromHours(6),
            unreachableTtl: () => TimeSpan.FromMinutes(2),
            now: () => now);

        map.Learn("AA:BB", "CC:DD", reachable: true);
        now = now.AddMinutes(10); // well past the unreachable TTL, well within the reachable one
        Assert.Equal(RelayReachabilityMap.Reachability.Reachable, map.Get("AA:BB", "CC:DD"));
    }
}
