using OnVoyage.Platform.Contracts;

namespace OnVoyage.Creators.Application.Features;

/// <summary>Deletion and export of everything Creators holds about a traveler (F-22, ADR-0015).</summary>
public interface IDataRightsStore
{
    /// <summary>
    /// Deletes the traveler's follows and unlinks their reports (the reporter reference is dropped, the report stays). When the traveler is
    /// also a creator the whole profile goes (contents, associations, tips, followers) and the withdrawals are published in the same
    /// transaction: <c>CreatorPlaceLinkChangedV1</c> removed for each published association, then <c>CreatorUnpublishedV1</c>.
    /// </summary>
    Task DeleteTravelerAsync(Guid travelerId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The traveler's data as JSON: follows, reports made, and the creator profile with its contents, associations and tips when they have one.</summary>
    Task<string> ExportAsync(Guid travelerId, CancellationToken cancellationToken);

    Task<string> WritePartAsync(Guid exportId, string json, CancellationToken cancellationToken);
}

public static class TravelerDeletionRequestedHandler
{
    public const string Service = "creators";

    public static async Task<TravelerDataDeletedV1> Handle(TravelerDeletionRequestedV1 requested, IDataRightsStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        await store.DeleteTravelerAsync(requested.TravelerId, clock.GetUtcNow(), cancellationToken);
        return new TravelerDataDeletedV1(Guid.CreateVersion7(), clock.GetUtcNow(), requested.TravelerId, Service);
    }
}

public static class TravelerExportRequestedHandler
{
    public static async Task<TravelerExportPartReadyV1> Handle(TravelerExportRequestedV1 requested, IDataRightsStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var path = await store.WritePartAsync(requested.ExportId, await store.ExportAsync(requested.TravelerId, cancellationToken), cancellationToken);
        return new TravelerExportPartReadyV1(Guid.CreateVersion7(), clock.GetUtcNow(), requested.ExportId, TravelerDeletionRequestedHandler.Service, path);
    }
}
