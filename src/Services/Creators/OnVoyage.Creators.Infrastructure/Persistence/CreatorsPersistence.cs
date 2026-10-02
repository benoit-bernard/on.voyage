using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnVoyage.Creators.Infrastructure.Persistence;

internal static class CreatorsPersistence
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CreatorsDbContext.Schema));
}

/// <summary>Used by <c>dotnet ef migrations</c> only. The connection string never reaches a real database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CreatorsDbContext>
{
    public CreatorsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CreatorsDbContext>();
        CreatorsPersistence.Configure(options, "Host=localhost;Database=design_time_only");
        return new CreatorsDbContext(options.Options);
    }
}
