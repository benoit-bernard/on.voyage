using OnVoyage.Factory.Application.Features.Snapshot;
using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Factory.Domain.Geo;

namespace OnVoyage.Factory.Application.Ports;

public interface IDestinationCatalog
{
    Task<DestinationConfig?> FindAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<DestinationConfig>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Runs the OSM import (osm2pgsql flex into <c>factory_raw</c>, §7.2). The place table is rebuilt from the raw table afterwards.</summary>
public interface IOsmImporter
{
    Task<OsmImportResult> ImportAsync(DestinationConfig destination, CancellationToken cancellationToken);
}

public interface IPlaceStore
{
    /// <summary>Creates or updates places from the raw OSM table. Existing places keep their scores, status and editorial fields.</summary>
    Task<(int Created, int Updated)> UpsertFromRawAsync(string destinationSlug, string rawTable, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or updates a place of a committed snapshot (no OpenStreetMap row behind it). The editorial importance is kept as an override so a
    /// later scoring run does not replace it. Reports whether anything changed (<see cref="SnapshotPlaceInput.ContentSha256"/>).
    /// </summary>
    Task<SnapshotPlaceChange> UpsertSnapshotPlaceAsync(SnapshotPlaceInput input, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaceRecord>> ListActiveAsync(string destinationSlug, CancellationToken cancellationToken);

    Task<PlaceRecord?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaceRecord>> ListAsync(string destinationSlug, PlaceStatus? status, int limit, CancellationToken cancellationToken);

    Task SaveEnrichmentAsync(IReadOnlyList<PlaceEnrichment> enrichments, IReadOnlyDictionary<string, long> annualPageviewsByQid, CancellationToken cancellationToken);

    Task SaveScoringAsync(IReadOnlyList<PlaceScoring> scorings, CancellationToken cancellationToken);

    Task<IReadOnlyList<(string Code, double Weight)>> GetInterestsAsync(Guid placeId, CancellationToken cancellationToken);

    Task MergeAsync(Guid keptPlaceId, Guid otherPlaceId, DedupLink link, CancellationToken cancellationToken);

    Task AddProposalAsync(DedupLink link, CancellationToken cancellationToken);

    Task<IReadOnlyList<DedupLink>> ListAllDedupLinksAsync(CancellationToken cancellationToken);

    Task<PlaceScoreDetail> GetScoreDetailAsync(Guid placeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DedupLink>> ListDedupLinksAsync(string destinationSlug, bool proposalsOnly, CancellationToken cancellationToken);

    Task<bool> RevertMergeAsync(Guid linkId, CancellationToken cancellationToken);

    Task SetEditorialAsync(Guid placeId, int? importanceOverride, bool? saturated, CancellationToken cancellationToken);

    Task SetEthicsAsync(Guid placeId, bool fragile, bool accessRegulated, CancellationToken cancellationToken);

    /// <summary>Replaces the place's interests with the editor's vector; a place waiting for classification review becomes a candidate.</summary>
    Task SetEditorInterestsAsync(Guid placeId, IReadOnlyDictionary<string, double> weights, CancellationToken cancellationToken);

    Task SetStatusAsync(Guid placeId, PlaceStatus status, CancellationToken cancellationToken);

    /// <summary>Persists the new published version and the integration event in one transaction (outbox).</summary>
    Task PublishAsync(Guid placeId, int version, object integrationEvent, CancellationToken cancellationToken);

    Task UnpublishAsync(Guid placeId, int version, object integrationEvent, CancellationToken cancellationToken);
}

public sealed record PlaceScoreDetail(int Importance, int Percentile, bool HiddenGem, int Offpeak, int Shoulder, int Peak, bool Fragile, bool AccessRegulated);

public interface IWikidataClient
{
    /// <summary>One request for up to 200 QIDs (§7.3). Unknown QIDs are simply absent from the result.</summary>
    Task<IReadOnlyList<PlaceEnrichment>> GetEntitiesAsync(IReadOnlyList<string> qids, CancellationToken cancellationToken);
}

public interface IPageviewsClient
{
    /// <summary>Sum of the last twelve full months of views of a Wikipedia article.</summary>
    Task<long> GetAnnualViewsAsync(string language, string title, CancellationToken cancellationToken);
}

public interface IClassificationRuleProvider
{
    ClassificationRuleSet Current { get; }
}

/// <summary>Structured-output fallback of §7.5. Implementations must restrict the answer to taxonomy codes. Returns <c>null</c> when unavailable.</summary>
public interface IPlaceModelClassifier
{
    Task<ModelClassification?> ClassifyAsync(PlaceDescription place, CancellationToken cancellationToken);
}
