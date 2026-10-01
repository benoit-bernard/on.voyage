using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnVoyage.Insights.Infrastructure.Persistence;

internal static class InsightsPersistence
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", InsightsDbContext.Schema));
}

/// <summary>Used by <c>dotnet ef migrations</c> only. The connection string never reaches a real database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<InsightsDbContext>
{
    public InsightsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InsightsDbContext>();
        InsightsPersistence.Configure(options, "Host=localhost;Database=design_time_only");
        return new InsightsDbContext(options.Options);
    }
}
