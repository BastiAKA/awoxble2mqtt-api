using AwoxController.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AwoxController.Data;

public static class AwoxDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the device registry. The provider is chosen by <c>Database:Provider</c>:
    ///   "mysql" (default) → Oracle MySql.EntityFrameworkCore, connection from <c>ConnectionStrings:AwoxDb</c>;
    ///   "sqlite"          → a local file (<c>ConnectionStrings:AwoxDb</c> or default "Data Source=awox.db"),
    ///                       so the app runs with zero DB server (dev / fallback).
    /// </summary>
    public static IServiceCollection AddAwoxData(this IServiceCollection services, IConfiguration config)
    {
        var provider = (config["Database:Provider"] ?? "mysql").Trim().ToLowerInvariant();
        var conn = config.GetConnectionString("AwoxDb");

        services.AddDbContext<AwoxDbContext>(opt =>
        {
            if (provider == "sqlite")
                opt.UseSqlite(string.IsNullOrWhiteSpace(conn) ? "Data Source=awox.db" : conn);
            else
                opt.UseMySQL(conn ?? throw new InvalidOperationException(
                    "Missing connection string 'ConnectionStrings:AwoxDb' for the MySQL provider."));
        });

        services.AddScoped<IDeviceStore, DeviceStore>();
        services.AddScoped<ISceneStore, SceneStore>();
        services.AddHttpClient<AwoxCloudClient>();
        return services;
    }

    /// <summary>
    /// Brings the schema up to date. SQLite always uses <c>EnsureCreated</c>. For MySQL the default
    /// is versioned EF migrations, but <paramref name="ensureCreated"/> forces <c>EnsureCreated</c> —
    /// needed on MariaDB, where Oracle's provider crashes in the migration-lock step
    /// (GET_LOCK returns NULL → InvalidCast). Set <c>Database:Bootstrap=ensureCreated</c> for MariaDB.
    /// </summary>
    public static async Task EnsureDatabaseAsync(this AwoxDbContext db, bool ensureCreated = false, CancellationToken ct = default)
    {
        if (ensureCreated || db.Database.IsSqlite())
            await db.Database.EnsureCreatedAsync(ct);
        else
            await db.Database.MigrateAsync(ct);
    }
}
