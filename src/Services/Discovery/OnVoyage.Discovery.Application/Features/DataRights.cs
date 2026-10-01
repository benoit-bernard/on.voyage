using OnVoyage.Platform.Contracts;

namespace OnVoyage.Discovery.Application.Features;

/// <summary>Deletion and export of everything Discovery holds about a traveler (F-22, T-507).</summary>
public interface IDataRightsStore
{
    /// <summary>Deletes the traveler and, by cascade, vector, interactions, ratings, visits, impressions and wishes. True when something existed.</summary>
    Task<bool> DeleteTravelerAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>The traveler's data as JSON: settings, vector, interactions, wishes, visits, ratings, impressions.</summary>
    Task<string> ExportAsync(Guid travelerId, CancellationToken cancellationToken);

    Task<string> WritePartAsync(Guid exportId, string json, CancellationToken cancellationToken);
}

public static class TravelerDeletionRequestedHandler
{
    public const string Service = "discovery";

    public static async Task<TravelerDataDeletedV1> Handle(TravelerDeletionRequestedV1 requested, IDataRightsStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        await store.DeleteTravelerAsync(requested.TravelerId, cancellationToken);
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
