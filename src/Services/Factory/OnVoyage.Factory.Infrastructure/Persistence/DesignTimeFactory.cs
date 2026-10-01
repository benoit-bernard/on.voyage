using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnVoyage.Factory.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef migrations</c> only.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>();
        FactoryPersistence.Configure(options, "Host=localhost;Database=design_time_only");
        return new FactoryDbContext(options.Options);
    }
}
