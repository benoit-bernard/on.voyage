using System.Text.Json;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Insights.Application.IntegrationEvents;

/// <summary>Keeps the local config snapshot in step with Platform. Redelivery and reordering are harmless: only newer versions apply.</summary>
public static class ConfigChangedHandler
{
    public static Task Handle(ConfigChangedV1 changed, IConfigSnapshotStore store, CancellationToken cancellationToken) =>
        store.ApplyAsync(changed.Key, changed.ValueJson, changed.Version, cancellationToken);
}

/// <summary>Projects the statistics consent (§16.3). Other kinds (ads personalization) do not concern Insights.</summary>
public static class ConsentChangedHandler
{
    public const string AnalyticsKind = "analytics";

    public static Task Handle(ConsentChangedV1 changed, IConsentProjectionWriter writer, CancellationToken cancellationToken) =>
        changed.Kind == AnalyticsKind
            ? writer.ApplyAsync(changed.TravelerId, changed.Granted, changed.OccurredAt, cancellationToken)
            : Task.CompletedTask;
}

/// <summary>Right to erasure (F-22): deletes the traveler's events and consent projection, then confirms to Platform. Repeating it is harmless.</summary>
public static class TravelerDeletionRequestedHandler
{
    public const string Service = "insights";

    /// <remarks>The confirmation is published by the store, in the transaction of the deletion: either both happen or neither does.</remarks>
    public static Task Handle(TravelerDeletionRequestedV1 requested, ITravelerDataStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        store.DeleteAsync(requested.TravelerId, new TravelerDataDeletedV1(Guid.CreateVersion7(), clock.GetUtcNow(), requested.TravelerId, Service), cancellationToken);
}

/// <summary>Right of access (F-22): writes the traveler's events and consent as one JSON part and tells Platform where it is.</summary>
public static class TravelerExportRequestedHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<TravelerExportPartReadyV1> Handle(TravelerExportRequestedV1 requested, ITravelerDataStore store, IExportPartWriter writer, TimeProvider clock, CancellationToken cancellationToken)
    {
        var data = await store.ReadAsync(requested.TravelerId, cancellationToken);
        var document = new
        {
            service = TravelerDeletionRequestedHandler.Service,
            analyticsConsent = data.AnalyticsConsent,
            consentUpdatedAt = data.ConsentUpdatedAt,
            events = data.Events.Select(e => new { e.Id, e.SessionId, e.Name, props = JsonDocument.Parse(e.PropsJson).RootElement, e.AppVersion, e.Platform, e.OccurredAt }),
        };

        var path = await writer.WriteAsync(requested.ExportId, JsonSerializer.Serialize(document, Json), cancellationToken);
        return new TravelerExportPartReadyV1(Guid.CreateVersion7(), clock.GetUtcNow(), requested.ExportId, TravelerDeletionRequestedHandler.Service, path);
    }
}
