namespace AwoxController.Core.Interfaces;

/// <summary>Reachability helpers over the live <see cref="ILightStateCache"/>.</summary>
public static class LightStateCacheExtensions
{
    /// <summary>
    /// True when a lamp MAC has a cached sighting newer than <paramref name="within"/> — i.e. it
    /// advertised (or was commanded) recently enough to be considered powered and in range. A MAC that
    /// was never observed this run, or whose last sighting is older than the window, is not fresh.
    /// </summary>
    public static bool IsFresh(this ILightStateCache cache, string? mac, TimeSpan within)
        => !string.IsNullOrWhiteSpace(mac)
           && cache.TryGet(mac, out var state)
           && DateTime.UtcNow - state.LastUpdatedUtc <= within;
}
