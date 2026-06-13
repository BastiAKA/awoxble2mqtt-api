using System.Collections.Concurrent;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;

namespace AwoxController.Api.Notifications;

/// <summary>
/// Process-lifetime, lock-free <see cref="ILightStateCache"/>. Bounded by the number of distinct lamps
/// (one entry per MAC), so it never grows unbounded. Lost on restart — that's fine: advertising lamps
/// repopulate it within a scan tick or two, and nothing here is worth an SD-card write.
/// </summary>
public sealed class InMemoryLightStateCache : ILightStateCache
{
    private readonly ConcurrentDictionary<string, LightState> _byMac = new();

    public void Set(string mac, LightState state) => _byMac[Normalize(mac)] = state;

    public bool TryGet(string mac, out LightState state) => _byMac.TryGetValue(Normalize(mac), out state!);

    /// <summary>MAC without separators, lower-cased — so "A4:C1:.." and "a4c138.." match.</summary>
    internal static string Normalize(string mac) => mac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
}
