using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

internal static class DiscoveryPersistence
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", DiscoveryDbContext.Schema));
}

/// <summary>Used by <c>dotnet ef migrations</c> only. The connection string never reaches a real database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DiscoveryDbContext>
{
    public DiscoveryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>();
        DiscoveryPersistence.Configure(options, "Host=localhost;Database=design_time_only");
        return new DiscoveryDbContext(options.Options);
    }
}
