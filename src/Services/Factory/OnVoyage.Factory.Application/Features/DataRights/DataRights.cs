using OnVoyage.Platform.Contracts;

namespace OnVoyage.Factory.Application.Features.DataRights;

/// <summary>What Factory keeps about a traveler: the reports they sent, nothing else (§16.2).</summary>
public interface IDataRightsStore
{
    /// <summary>Detaches the traveler from their reports. The report texts stay (they concern a story, not a person); the link to the traveler goes.</summary>
    Task<int> AnonymizeReportsAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>JSON of the traveler's reports (story, reason, date, status).</summary>
    Task<string> ExportReportsAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Writes the export part and returns its path in the private exports area.</summary>
    Task<string> WritePartAsync(Guid exportId, string json, CancellationToken cancellationToken);
}

public static class TravelerDeletionRequestedHandler
{
    public const string Service = "factory";

    /// <summary>Reports are anonymised (F-22), then Platform is told. Running it twice changes nothing.</summary>
    public static async Task<TravelerDataDeletedV1> Handle(TravelerDeletionRequestedV1 requested, IDataRightsStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        await store.AnonymizeReportsAsync(requested.TravelerId, cancellationToken);
        return new TravelerDataDeletedV1(Guid.CreateVersion7(), clock.GetUtcNow(), requested.TravelerId, Service);
    }
}

public static class TravelerExportRequestedHandler
{
    public static async Task<TravelerExportPartReadyV1> Handle(TravelerExportRequestedV1 requested, IDataRightsStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var json = await store.ExportReportsAsync(requested.TravelerId, cancellationToken);
        var path = await store.WritePartAsync(requested.ExportId, json, cancellationToken);
        return new TravelerExportPartReadyV1(Guid.CreateVersion7(), clock.GetUtcNow(), requested.ExportId, TravelerDeletionRequestedHandler.Service, path);
    }
}
