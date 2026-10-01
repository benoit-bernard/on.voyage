using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application.Features.DataRights;
using OnVoyage.ServiceDefaults.Exports;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed class DataRightsStore(FactoryDbContext db, ExportStorage exports) : IDataRightsStore
{
    /// <summary>The reports of a deleted traveler keep this identifier: a report is evidence about a story, and the person is no longer known.</summary>
    public static readonly Guid Anonymous = Guid.Empty;

    public async Task<int> AnonymizeReportsAsync(Guid travelerId, CancellationToken cancellationToken) =>
        await db.StoryReports.Where(r => r.TravelerId == travelerId).ExecuteUpdateAsync(set => set.SetProperty(r => r.TravelerId, Anonymous), cancellationToken);

    public async Task<string> ExportReportsAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var reports = await db.StoryReports.AsNoTracking().Where(r => r.TravelerId == travelerId).OrderBy(r => r.CreatedAt)
            .Select(r => new { storyId = r.StoryId, reason = r.Reason, createdAt = r.CreatedAt, status = r.Status }).ToListAsync(cancellationToken);
        return JsonSerializer.Serialize(new { reports });
    }

    public Task<string> WritePartAsync(Guid exportId, string json, CancellationToken cancellationToken) => exports.WriteAsync(exportId, "factory", json, cancellationToken);
}
