using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AwoxController.Data;

/// <summary>
/// Design-time factory so `dotnet ef migrations add ...` can build the context without the app's
/// DI. Generating migrations needs only the provider + server version, not a reachable database.
/// Override the connection with env var AWOX_DB_CONNECTION when applying migrations to a real DB.
/// </summary>
public sealed class AwoxDbContextFactory : IDesignTimeDbContextFactory<AwoxDbContext>
{
    public AwoxDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("AWOX_DB_CONNECTION")
                   ?? "server=localhost;port=3306;database=awox;user=awox;password=awox";
        var options = new DbContextOptionsBuilder<AwoxDbContext>()
            .UseMySQL(conn)
            .Options;
        return new AwoxDbContext(options);
    }
}
