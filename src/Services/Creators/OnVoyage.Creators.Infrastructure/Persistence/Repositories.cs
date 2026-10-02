using Microsoft.EntityFrameworkCore;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Infrastructure.Persistence;

internal sealed class CreatorRepository(CreatorsDbContext db) : ICreatorRepository
{
    public async Task<Creator?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Creators.FindAsync([id], cancellationToken))?.ToDomain();

    public async Task<Creator?> FindByHandleAsync(string handle, CancellationToken cancellationToken) =>
        (await db.Creators.FirstOrDefaultAsync(creator => creator.Handle == handle, cancellationToken))?.ToDomain();

    public async Task<Creator?> FindByAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await db.Creators.FirstOrDefaultAsync(creator => creator.AccountId == accountId, cancellationToken))?.ToDomain();

    public async Task<IReadOnlySet<string>> HandlesStartingWithAsync(string prefix, CancellationToken cancellationToken) =>
        new HashSet<string>(
            await db.Creators.AsNoTracking().Where(creator => EF.Functions.ILike(creator.Handle, prefix + "%")).Select(creator => creator.Handle).ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

    public async Task StageAsync(Creator creator, CancellationToken cancellationToken)
    {
        var row = await db.Creators.FindAsync([creator.Id], cancellationToken);
        if (row is null)
        {
            row = new CreatorRow { Id = creator.Id };
            db.Creators.Add(row);
        }

        creator.CopyTo(row);
    }
}

internal sealed class ContentRepository(CreatorsDbContext db, TimeProvider clock) : IContentRepository
{
    public async Task<ContentItem?> FindContentAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Contents.FindAsync([id], cancellationToken))?.ToDomain();

    public Task<bool> ContentExistsAsync(string platform, string externalId, CancellationToken cancellationToken) =>
        db.Contents.AnyAsync(content => content.Platform == platform && content.ExternalId == externalId, cancellationToken);

    public async Task<IReadOnlyList<ContentItem>> ListContentsAsync(Guid creatorId, CancellationToken cancellationToken) =>
        [.. (await db.Contents.Where(content => content.CreatorId == creatorId).OrderByDescending(content => content.CreatedAt).ToListAsync(cancellationToken)).Select(row => row.ToDomain())];

    public async Task StageContentAsync(ContentItem content, CancellationToken cancellationToken)
    {
        var row = await db.Contents.FindAsync([content.Id], cancellationToken);
        if (row is null)
        {
            row = new ContentRow { Id = content.Id };
            db.Contents.Add(row);
        }

        content.CopyTo(row, clock.GetUtcNow());
    }

    public async Task<PlaceLink?> FindLinkAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.PlaceLinks.FindAsync([id], cancellationToken))?.ToDomain();

    public async Task<PlaceLink?> FindLinkAsync(Guid creatorId, Guid poiId, Guid? contentId, int? startSeconds, CancellationToken cancellationToken) =>
        (await db.PlaceLinks.FirstOrDefaultAsync(link => link.CreatorId == creatorId && link.PoiId == poiId && link.ContentId == contentId && link.StartS == startSeconds, cancellationToken))?.ToDomain();

    public async Task<IReadOnlyList<PlaceLink>> ListLinksAsync(Guid creatorId, CancellationToken cancellationToken) =>
        [.. (await db.PlaceLinks.Where(link => link.CreatorId == creatorId).ToListAsync(cancellationToken)).Select(row => row.ToDomain())];

    public async Task<IReadOnlyList<PlaceLink>> ListLinksOfContentAsync(Guid contentId, CancellationToken cancellationToken) =>
        [.. (await db.PlaceLinks.Where(link => link.ContentId == contentId).ToListAsync(cancellationToken)).Select(row => row.ToDomain())];

    public async Task StageLinkAsync(PlaceLink link, CancellationToken cancellationToken)
    {
        var row = await db.PlaceLinks.FindAsync([link.Id], cancellationToken);
        if (row is null)
        {
            row = new PlaceLinkRow { Id = link.Id };
            db.PlaceLinks.Add(row);
        }

        link.CopyTo(row);
    }

    public async Task DeleteLinkAsync(Guid linkId, CancellationToken cancellationToken)
    {
        if (await db.PlaceLinks.FindAsync([linkId], cancellationToken) is { } row)
        {
            db.PlaceLinks.Remove(row);
        }
    }

    public async Task<CreatorTip?> FindTipAsync(Guid creatorId, Guid poiId, CancellationToken cancellationToken) =>
        (await db.Tips.FindAsync([creatorId, poiId], cancellationToken))?.ToDomain();

    public async Task<CreatorTip?> FindTipAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Tips.FirstOrDefaultAsync(tip => tip.Id == id, cancellationToken))?.ToDomain();

    public async Task StageTipAsync(CreatorTip tip, CancellationToken cancellationToken)
    {
        var row = await db.Tips.FindAsync([tip.CreatorId, tip.PoiId], cancellationToken);
        if (row is null)
        {
            row = new TipRow { Id = tip.Id, CreatorId = tip.CreatorId, PoiId = tip.PoiId };
            db.Tips.Add(row);
        }

        row.Text = tip.Text;
        row.UpdatedAt = tip.UpdatedAt;
        row.Status = tip.Status;
    }

    public async Task DeleteTipAsync(Guid creatorId, Guid poiId, CancellationToken cancellationToken)
    {
        if (await db.Tips.FindAsync([creatorId, poiId], cancellationToken) is { } row)
        {
            db.Tips.Remove(row);
        }
    }
}

internal sealed class ModerationRepository(CreatorsDbContext db) : IModerationRepository
{
    public async Task<ModerationCase?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Cases.FindAsync([id], cancellationToken))?.ToDomain();

    public async Task<ModerationCase?> FindOpenAsync(Guid reporter, string targetType, Guid targetId, string reason, CancellationToken cancellationToken) =>
        (await db.Cases.AsNoTracking().FirstOrDefaultAsync(
            item => item.ReporterRef == reporter && item.TargetType == targetType && item.TargetId == targetId && item.Reason == reason && item.Status == ModerationStatuses.Open,
            cancellationToken))?.ToDomain();

    public Task<bool> TargetExistsAsync(string targetType, Guid targetId, CancellationToken cancellationToken) => targetType switch
    {
        ModerationTargets.Creator => db.Creators.AnyAsync(creator => creator.Id == targetId, cancellationToken),
        ModerationTargets.Content => db.Contents.AnyAsync(content => content.Id == targetId, cancellationToken),
        ModerationTargets.PlaceLink => db.PlaceLinks.AnyAsync(link => link.Id == targetId, cancellationToken),
        ModerationTargets.Tip => db.Tips.AnyAsync(tip => tip.Id == targetId, cancellationToken),
        _ => Task.FromResult(false),
    };

    public async Task StageAsync(ModerationCase moderationCase, CancellationToken cancellationToken)
    {
        var row = await db.Cases.FindAsync([moderationCase.Id], cancellationToken);
        if (row is null)
        {
            row = new ModerationCaseRow { Id = moderationCase.Id };
            db.Cases.Add(row);
        }

        moderationCase.CopyTo(row);
    }
}

internal sealed class FollowRepository(CreatorsDbContext db) : IFollowRepository
{
    public async Task<bool> StageAsync(Guid travelerId, Guid creatorId, bool following, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var row = await db.Follows.FindAsync([travelerId, creatorId], cancellationToken);
        if (following)
        {
            if (row is not null)
            {
                return false;
            }

            db.Follows.Add(new FollowRow { TravelerId = travelerId, CreatorId = creatorId, FollowedAt = at });
            return true;
        }

        if (row is null)
        {
            return false;
        }

        db.Follows.Remove(row);
        return true;
    }
}

internal sealed class PoiDirectory(CreatorsDbContext db, TimeProvider clock) : IPoiDirectory, IPoiDirectoryWriter
{
    public async Task<PoiEntry?> FindAsync(Guid poiId, CancellationToken cancellationToken) =>
        (await db.Pois.AsNoTracking().FirstOrDefaultAsync(poi => poi.PoiId == poiId, cancellationToken))?.ToDomain();

    public async Task<IReadOnlyList<OnVoyage.Creators.Contracts.PoiSearchResultDto>> SearchAsync(string query, string? destinationSlug, int limit, CancellationToken cancellationToken)
    {
        // Substring search served by the trigram index; the text is already lowercase and accent-free.
        var pattern = "%" + query.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var pois = db.Pois.AsNoTracking().Where(poi => EF.Functions.Like(poi.SearchText, pattern));
        if (destinationSlug is not null)
        {
            pois = pois.Where(poi => poi.DestinationSlug == destinationSlug);
        }

        return await pois.OrderByDescending(poi => poi.IsPublished).ThenByDescending(poi => poi.ImportanceScore).ThenBy(poi => poi.Name).Take(limit)
            .Select(poi => new OnVoyage.Creators.Contracts.PoiSearchResultDto(poi.PoiId, poi.Name, poi.City, poi.DestinationSlug, poi.IsPublished))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ApplyAsync(PoiEntry entry, CancellationToken cancellationToken)
    {
        var row = await db.Pois.FindAsync([entry.PoiId], cancellationToken);
        if (row is not null && row.Version >= entry.Version)
        {
            return false;
        }

        if (row is null)
        {
            row = new PoiDirectoryRow { PoiId = entry.PoiId };
            db.Pois.Add(row);
        }

        entry.CopyTo(row, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolations.Of(exception) is not null)
        {
            // Two deliveries of the same place at once: the other one won, and the event is a duplicate.
            db.Entry(row).State = EntityState.Detached;
            return false;
        }

        return true;
    }
}
