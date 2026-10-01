using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef migrations</c> only. The connection string never reaches a real database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogPersistence.Configure(options, "Host=localhost;Database=design_time_only");
        return new CatalogDbContext(options.Options);
    }
}
