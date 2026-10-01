using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

internal static class CatalogPersistence
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", CatalogDbContext.Schema);
        });
}
