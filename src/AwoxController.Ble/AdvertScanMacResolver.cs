using AwoxController.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AwoxController.Ble;

/// <summary>
/// Shared support for the passive advert status scanners (BlueZ on Linux, WinRT on Windows): builds the
/// MAC→canonical-MAC scan map from the device registry (falling back to <c>AwoxBle:Devices</c> config),
/// and self-heals a registry MeshId the cloud import left at 0 from the lamp's own advert. Holds the
/// "still needs fixing" set, so it is intended to be used by ONE scan loop (single-threaded).
/// </summary>
internal sealed class AdvertScanMacResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AwoxBleOptions _options;
    private readonly ILogger _logger;

    /// <summary>normMac → lampId for lamps whose registry MeshId is still 0 (to self-heal from adverts).
    /// Rebuilt with the MAC map; entries are removed once fixed.</summary>
    private Dictionary<string, int> _meshIdToFix = new(StringComparer.OrdinalIgnoreCase);

    public AdvertScanMacResolver(IServiceScopeFactory scopes, AwoxBleOptions options, ILogger logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Builds the MAC→MAC scan map from the device registry (every lamp with a MAC), falling back to the
    /// <c>AwoxBle:Devices</c> config when no registry/DB is available. Keys are separator-stripped,
    /// upper-cased MACs; the value is the lamp's canonical MAC, used as the device id downstream. Also
    /// (re)builds the MeshId-fix set.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> BuildMacMapAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetService<IDeviceStore>();
            if (store is not null)
            {
                var lamps = await store.GetLampsAsync(ct);
                var map = lamps
                    .Where(l => !string.IsNullOrWhiteSpace(l.Mac))
                    .GroupBy(l => NormalizeMac(l.Mac))
                    .ToDictionary(g => g.Key, g => g.First().Mac, StringComparer.OrdinalIgnoreCase);

                // Lamps whose registry MeshId is 0 are the cloud-import bug (0 = broadcast, not a real
                // per-lamp id). The lamp broadcasts its TRUE mesh id in the advert, so we self-heal it
                // from the scan (see TryFixMeshIdFromAdvertAsync). Track which MACs still need fixing.
                _meshIdToFix = lamps
                    .Where(l => l.MeshId == 0 && !string.IsNullOrWhiteSpace(l.Mac))
                    .GroupBy(l => NormalizeMac(l.Mac))
                    .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

                if (map.Count > 0)
                    return map;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Advert scan: could not read the device registry; falling back to config devices.");
        }

        return _options.Devices
            .Where(d => !string.IsNullOrWhiteSpace(d.Mac))
            .GroupBy(d => NormalizeMac(d.Mac))
            .ToDictionary(g => g.Key, g => g.First().Mac, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One-time self-heal of a registry MeshId that the cloud import left at 0: the advert carries the
    /// lamp's real mesh id, so write it back to the DB and drop the MAC from the fix set (one write per
    /// affected lamp, then never again). Correct per-lamp ids are what individual addressing/relay need.
    /// </summary>
    public async Task TryFixMeshIdFromAdvertAsync(string normMac, ushort advertMeshId)
    {
        if (advertMeshId == 0 || !_meshIdToFix.TryGetValue(normMac, out var lampId))
            return;

        _meshIdToFix.Remove(normMac); // remove first so a failure doesn't retry every tick
        try
        {
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetService<IDeviceStore>();
            if (store is null) return;

            var lamp = await store.GetLampByIdAsync(lampId);
            if (lamp is null || lamp.MeshId != 0) return; // gone or already fixed elsewhere

            lamp.MeshId = advertMeshId;
            await store.UpdateLampAsync(lamp);
            _logger.LogInformation("Self-healed MeshId for '{Name}' ({Mac}) from advert: 0 → {MeshId} (0x{Hex:X4}).",
                lamp.Name, lamp.Mac, advertMeshId, advertMeshId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist self-healed MeshId for lamp {LampId}.", lampId);
        }
    }

    public static string NormalizeMac(string mac)
        => mac.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
}
