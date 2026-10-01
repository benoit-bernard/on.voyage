using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal static class FactoryPersistence
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", FactoryDbContext.Schema);
        });
}
