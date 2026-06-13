using System.Collections.Concurrent;
using System.Globalization;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AwoxController.Data;

/// <summary>
/// EF Core implementation of <see cref="IAppSettings"/>. A singleton with an in-memory cache: reads
/// hit the cache (loaded once, lazily), writes go through the scoped <see cref="AwoxDbContext"/> and
/// update the cache. Because the cache load is wrapped in a try/catch, this works even when no
/// database is registered/reachable — reads then just return the caller's fallback.
/// </summary>
public sealed class AppSettingsService : IAppSettings
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppSettingsService> _logger;
    private readonly object _loadGate = new();
    private volatile ConcurrentDictionary<string, string>? _cache;

    public AppSettingsService(IServiceScopeFactory scopeFactory, ILogger<AppSettingsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Reload() => _cache = null;

    public string? GetString(string key, string? fallback = null)
        => Cache().TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback)
        => Cache().TryGetValue(key, out var v)
           && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : fallback;

    public double GetDouble(string key, double fallback)
        => Cache().TryGetValue(key, out var v)
           && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;

    public bool GetBool(string key, bool fallback)
    {
        if (!Cache().TryGetValue(key, out var v)) return fallback;
        if (bool.TryParse(v, out var b)) return b;
        return v.Trim() switch { "1" => true, "0" => false, _ => fallback };
    }

    public async Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AwoxDbContext>();
        return await db.Settings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);
    }

    // The Description column is VARCHAR(256) (see AwoxDbContext). Clamp here so an over-long description
    // can never throw on INSERT and silently abort the rest of the startup seeding (that bug cost us the
    // ble.connect_settle_ms seed). Truncation is harmless — descriptions are human hints, not data.
    private const int DescriptionMaxLength = 256;

    private static string? ClampDescription(string? description)
        => description is { Length: > DescriptionMaxLength } ? description[..DescriptionMaxLength] : description;

    public async Task SetAsync(string key, string value, string? description = null, CancellationToken ct = default)
    {
        description = ClampDescription(description);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AwoxDbContext>();
        var existing = await db.Settings.FindAsync([key], ct);
        if (existing is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value, Description = description, UpdatedUtc = DateTime.UtcNow });
        }
        else
        {
            existing.Value = value;
            if (description is not null) existing.Description = description;
            existing.UpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        Cache()[key] = value;
    }

    public async Task EnsureDefaultAsync(string key, string value, string? description = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AwoxDbContext>();
        if (await db.Settings.FindAsync([key], ct) is not null) return;
        db.Settings.Add(new AppSetting { Key = key, Value = value, Description = ClampDescription(description), UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        Cache()[key] = value;
    }

    private ConcurrentDictionary<string, string> Cache()
    {
        var c = _cache;
        if (c is not null) return c;

        lock (_loadGate)
        {
            if (_cache is not null) return _cache;

            var dict = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AwoxDbContext>();
                foreach (var s in db.Settings.AsNoTracking().ToList())
                    dict[s.Key] = s.Value;
            }
            catch (Exception ex)
            {
                // No DB configured/reachable — fall back to code defaults. Non-fatal.
                _logger.LogWarning(ex, "Could not load app_settings; using code defaults.");
            }

            _cache = dict;
            return dict;
        }
    }
}
