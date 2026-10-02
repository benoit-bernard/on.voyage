using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Ports;

/// <summary>A unique index refused the write (a handle or a content taken in the meantime). <c>Constraint</c> is the name of the index.</summary>
public sealed class UniqueConflictException(string constraint) : Exception($"Unique constraint {constraint} violated.")
{
    public string Constraint { get; } = constraint;
}

/// <summary>
/// Repositories stage their changes; the unit of work saves them with the integration events in one transaction (Wolverine outbox), so a
/// change and its events happen together or not at all.
/// </summary>
public interface ICreatorsUnitOfWork
{
    /// <summary>Saves what the repositories staged and publishes <paramref name="events"/>. Throws <see cref="UniqueConflictException"/> on a unique violation.</summary>
    Task CommitAsync(IReadOnlyList<object> events, CancellationToken cancellationToken);

    /// <summary>Saves what is staged without publishing anything yet, inside the transaction that <see cref="CommitAsync"/> will close (for an ordered rename).</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

public interface ICreatorRepository
{
    Task<Creator?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The creator holding the handle, whatever its case.</summary>
    Task<Creator?> FindByHandleAsync(string handle, CancellationToken cancellationToken);

    Task<Creator?> FindByAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> HandlesStartingWithAsync(string prefix, CancellationToken cancellationToken);

    Task StageAsync(Creator creator, CancellationToken cancellationToken);
}

public interface IContentRepository
{
    Task<ContentItem?> FindContentAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ContentExistsAsync(string platform, string externalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentItem>> ListContentsAsync(Guid creatorId, CancellationToken cancellationToken);

    Task StageContentAsync(ContentItem content, CancellationToken cancellationToken);

    Task<PlaceLink?> FindLinkAsync(Guid id, CancellationToken cancellationToken);

    Task<PlaceLink?> FindLinkAsync(Guid creatorId, Guid poiId, Guid? contentId, int? startSeconds, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaceLink>> ListLinksAsync(Guid creatorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaceLink>> ListLinksOfContentAsync(Guid contentId, CancellationToken cancellationToken);

    Task StageLinkAsync(PlaceLink link, CancellationToken cancellationToken);

    Task DeleteLinkAsync(Guid linkId, CancellationToken cancellationToken);

    Task<CreatorTip?> FindTipAsync(Guid creatorId, Guid poiId, CancellationToken cancellationToken);

    Task<CreatorTip?> FindTipAsync(Guid id, CancellationToken cancellationToken);

    Task StageTipAsync(CreatorTip tip, CancellationToken cancellationToken);

    Task DeleteTipAsync(Guid creatorId, Guid poiId, CancellationToken cancellationToken);
}

/// <summary>The Creators copy of the catalog's places (<c>creators.poi_directory</c>). No coordinate is ever kept.</summary>
public interface IPoiDirectory
{
    Task<PoiEntry?> FindAsync(Guid poiId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PoiSearchResultDto>> SearchAsync(string query, string? destinationSlug, int limit, CancellationToken cancellationToken);
}

public interface IPoiDirectoryWriter
{
    /// <summary>Applies the place if it is newer than the stored one; false for a duplicate or stale event (idempotent consumption).</summary>
    Task<bool> ApplyAsync(PoiEntry entry, CancellationToken cancellationToken);
}

public interface IModerationRepository
{
    Task<ModerationCase?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>An open case from the same reporter on the same target for the same reason (a second report adds nothing).</summary>
    Task<ModerationCase?> FindOpenAsync(Guid reporter, string targetType, Guid targetId, string reason, CancellationToken cancellationToken);

    Task<bool> TargetExistsAsync(string targetType, Guid targetId, CancellationToken cancellationToken);

    Task StageAsync(ModerationCase moderationCase, CancellationToken cancellationToken);
}

public interface IFollowRepository
{
    /// <summary>Stages the follow (or its removal). False when the traveler was already in that state: nothing to save, nothing to publish.</summary>
    Task<bool> StageAsync(Guid travelerId, Guid creatorId, bool following, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side, straight to the contracts' DTOs. Public reads only ever return what F-26 allows: published creator, validated link, published place, content online.</summary>
public interface ICreatorQueries
{
    Task<CreatorPageDto?> GetPageAsync(string handle, Guid? viewer, CancellationToken cancellationToken);

    Task<CreatorListDto> ListPublishedAsync(string? destinationSlug, string? specialty, int offset, int limit, CancellationToken cancellationToken);

    Task<PoiCreatorsDto> ListForPoiAsync(Guid poiId, int limit, CancellationToken cancellationToken);

    Task<bool> IsPublishedAsync(Guid creatorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FollowedCreatorDto>> ListFollowsAsync(Guid travelerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminCreatorSummaryDto>> ListAdminAsync(string? status, string? search, int limit, CancellationToken cancellationToken);

    Task<AdminCreatorDetailDto?> GetAdminDetailAsync(Guid creatorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ModerationCaseDto>> ListModerationAsync(string? status, int limit, CancellationToken cancellationToken);

    Task<ModerationCaseDto?> GetModerationAsync(Guid caseId, CancellationToken cancellationToken);
}
