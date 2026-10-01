using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnVoyage.Platform.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef migrations</c> only.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>();
        PlatformPersistence.Configure(options, "Host=localhost;Database=design_time_only");
        return new PlatformDbContext(options.Options);
    }
}
