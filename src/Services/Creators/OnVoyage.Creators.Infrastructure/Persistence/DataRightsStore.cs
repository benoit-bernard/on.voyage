using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Domain;
using OnVoyage.ServiceDefaults.Exports;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Creators.Infrastructure.Persistence;

internal sealed class DataRightsStore(IDbContextOutbox<CreatorsDbContext> outbox, ExportStorage exports) : IDataRightsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private CreatorsDbContext Db => outbox.DbContext;

    public async Task DeleteTravelerAsync(Guid travelerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Db.Follows.RemoveRange(await Db.Follows.Where(follow => follow.TravelerId == travelerId).ToListAsync(cancellationToken));

        // The report stays (the decision may concern someone else) but nobody can tell who made it any more.
        foreach (var report in await Db.Cases.Where(item => item.ReporterRef == travelerId).ToListAsync(cancellationToken))
        {
            report.ReporterRef = null;
        }

        var creator = await Db.Creators.FirstOrDefaultAsync(row => row.AccountId == travelerId, cancellationToken);
        if (creator is not null)
        {
            var online = await Db.Contents.Where(content => content.CreatorId == creator.Id).ToDictionaryAsync(content => content.Id, cancellationToken);
            var published = await Db.PlaceLinks.Where(link => link.CreatorId == creator.Id && link.Status == PlaceLinkStatuses.Validated).ToListAsync(cancellationToken);
            foreach (var link in published.Where(link => link.ContentId is null || online[link.ContentId.Value].Status == ContentStatuses.Imported))
            {
                var content = link.ContentId is { } id ? online[id] : null;
                await outbox.PublishAsync(new OnVoyage.Creators.Contracts.CreatorPlaceLinkChangedV1(Guid.CreateVersion7(), now, creator.Id, link.PoiId, link.ContentId, content?.Kind ?? "tip", content?.IsCommercial ?? false, "removed"));
            }

            if (creator.Status == CreatorStatuses.Published)
            {
                await outbox.PublishAsync(new OnVoyage.Creators.Contracts.CreatorUnpublishedV1(Guid.CreateVersion7(), now, creator.Id, creator.Handle, "account_deleted"));
            }

            Db.Creators.Remove(creator); // contents, associations, tips and followers go with it (cascade)
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    public async Task<string> ExportAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var follows = await Db.Follows.AsNoTracking().Where(follow => follow.TravelerId == travelerId)
            .Join(Db.Creators, follow => follow.CreatorId, creator => creator.Id, (follow, creator) => new { creator.Handle, follow.FollowedAt })
            .OrderBy(item => item.FollowedAt).ToListAsync(cancellationToken);
        var reports = await Db.Cases.AsNoTracking().Where(item => item.ReporterRef == travelerId).OrderBy(item => item.CreatedAt)
            .Select(item => new { item.TargetType, item.TargetId, item.Reason, item.Status, item.Decision, item.CreatedAt }).ToListAsync(cancellationToken);

        object? creatorData = null;
        if (await Db.Creators.AsNoTracking().FirstOrDefaultAsync(row => row.AccountId == travelerId, cancellationToken) is { } creator)
        {
            creatorData = new
            {
                creator.Handle,
                creator.DisplayName,
                creator.Bio,
                creator.AvatarPath,
                creator.Languages,
                creator.Specialties,
                creator.DestinationIds,
                links = JsonSerializer.Deserialize<JsonElement>(creator.Links),
                creator.Status,
                creator.Founding,
                creator.TermsVersion,
                creator.TermsAcceptedAt,
                creator.TermsDocumentRef,
                creator.CreatedAt,
                contents = await Db.Contents.AsNoTracking().Where(content => content.CreatorId == creator.Id).OrderBy(content => content.CreatedAt)
                    .Select(content => new { content.Platform, content.Permalink, content.Title, content.CaptionExcerpt, content.PublishedAt, content.DurationS, content.Kind, content.IsCommercial, content.Status }).ToListAsync(cancellationToken),
                placeLinks = await Db.PlaceLinks.AsNoTracking().Where(link => link.CreatorId == creator.Id).OrderBy(link => link.CreatedAt)
                    .Select(link => new { link.PoiId, link.ContentId, link.StartS, link.Status, link.ValidatedAt }).ToListAsync(cancellationToken),
                tips = await Db.Tips.AsNoTracking().Where(tip => tip.CreatorId == creator.Id).OrderBy(tip => tip.UpdatedAt)
                    .Select(tip => new { tip.PoiId, tip.Text, tip.Status, tip.UpdatedAt }).ToListAsync(cancellationToken),
            };
        }

        return JsonSerializer.Serialize(new { service = "creators", follows, reports, creator = creatorData }, Json);
    }

    public Task<string> WritePartAsync(Guid exportId, string json, CancellationToken cancellationToken) => exports.WriteAsync(exportId, "creators", json, cancellationToken);
}
