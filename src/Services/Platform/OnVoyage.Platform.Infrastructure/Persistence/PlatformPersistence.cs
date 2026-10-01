using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Platform.Infrastructure.Persistence;

internal static class PlatformPersistence
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema));
}
