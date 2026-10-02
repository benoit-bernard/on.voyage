using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Infrastructure.Persistence;

/// <summary>
/// Reads for travelers and administrators. A traveler only ever sees a link that is <c>validated</c>, of a creator who is
/// <c>published</c>, to a place that is <c>published</c>, with its content (if any) <c>online</c> (F-26, F-28): that rule lives in
/// <see cref="Visible"/> and every public read goes through it.
/// </summary>
internal sealed class CreatorQueries(CreatorsDbContext db) : ICreatorQueries
{
    /// <summary>Followers are shown from this many (F-26); below, the page says "Nouveau créateur".</summary>
    public const int FollowerDisplayThreshold = 20;

    private const int RecentContentCount = 10;

    // A class with member initializers rather than a record with a constructor: EF Core composes further queries over it (Where, GroupBy).
    private sealed class VisibleRow
    {
        public Guid LinkId { get; init; }
        public Guid CreatorId { get; init; }
        public Guid PoiId { get; init; }
        public string PoiName { get; init; } = "";
        public string? City { get; init; }
        public Guid DestinationId { get; init; }
        public string DestinationSlug { get; init; } = "";
        public int? StartS { get; init; }
        public DateTimeOffset? ValidatedAt { get; init; }
        public Guid? ContentId { get; init; }
        public string? Platform { get; init; }
        public string? Kind { get; init; }
        public string? Title { get; init; }
        public string? Permalink { get; init; }
        public int? DurationS { get; init; }
        public string? CoverPath { get; init; }
        public bool? IsCommercial { get; init; }
        public DateTimeOffset? ContentPublishedAt { get; init; }
    }

    private IQueryable<VisibleRow> Visible() =>
        from link in db.PlaceLinks.AsNoTracking()
        where link.Status == PlaceLinkStatuses.Validated
        join creator in db.Creators.AsNoTracking() on link.CreatorId equals creator.Id
        where creator.Status == CreatorStatuses.Published
        join poi in db.Pois.AsNoTracking() on link.PoiId equals poi.PoiId
        where poi.IsPublished
        join content in db.Contents.AsNoTracking() on link.ContentId equals content.Id into contents
        from content in contents.DefaultIfEmpty()
        where content == null || content.Status == ContentStatuses.Imported
        select new VisibleRow
        {
            LinkId = link.Id,
            CreatorId = link.CreatorId,
            PoiId = link.PoiId,
            PoiName = poi.Name,
            City = poi.City,
            DestinationId = poi.DestinationId,
            DestinationSlug = poi.DestinationSlug,
            StartS = link.StartS,
            ValidatedAt = link.ValidatedAt,
            ContentId = content == null ? null : content.Id,
            Platform = content == null ? null : content.Platform,
            Kind = content == null ? null : content.Kind,
            Title = content == null ? null : content.Title,
            Permalink = content == null ? null : content.Permalink,
            DurationS = content == null ? null : content.DurationS,
            CoverPath = content == null ? null : content.CoverPath,
            IsCommercial = content == null ? null : content.IsCommercial,
            ContentPublishedAt = content == null ? null : content.PublishedAt,
        };

    private static CreatorContentDto? ToContent(VisibleRow row) => ToContent(row, withStart: true);

    /// <summary>The content of a link; <paramref name="withStart"/> false gives the content as a whole (no chapter), for "recent contents".</summary>
    private static CreatorContentDto? ToContent(VisibleRow row, bool withStart)
    {
        if (row.ContentId is null)
        {
            return null;
        }

        var start = withStart ? row.StartS : null;
        return new CreatorContentDto(row.ContentId.Value, row.Platform!, row.Kind!, row.Title!, ContentUrls.At(row.Platform!, row.Permalink!, start), start, row.DurationS, row.CoverPath, row.IsCommercial ?? false, row.ContentPublishedAt);
    }

    public async Task<CreatorPageDto?> GetPageAsync(string handle, Guid? viewer, CancellationToken cancellationToken)
    {
        var creator = await db.Creators.AsNoTracking().FirstOrDefaultAsync(row => row.Handle == handle && row.Status == CreatorStatuses.Published, cancellationToken);
        if (creator is null)
        {
            return null;
        }

        var rows = await Visible().Where(row => row.CreatorId == creator.Id).ToListAsync(cancellationToken);
        var tips = await db.Tips.AsNoTracking().Where(tip => tip.CreatorId == creator.Id && tip.Status == TipStatuses.Published).ToDictionaryAsync(tip => tip.PoiId, tip => tip.Text, cancellationToken);
        var places = rows.GroupBy(row => row.PoiId)
            .Select(group => new CreatorPlaceDto(
                group.Key,
                group.First().PoiName,
                group.First().City,
                tips.GetValueOrDefault(group.Key),
                [.. group.Select(ToContent).OfType<CreatorContentDto>().OrderByDescending(content => content.PublishedAt)]))
            .OrderBy(place => place.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var recent = rows.Where(row => row.ContentId is not null).DistinctBy(row => row.ContentId)
            .OrderByDescending(row => row.ContentPublishedAt).Take(RecentContentCount)
            .Select(row => ToContent(row, withStart: false)!).ToList();
        var followers = await db.Follows.CountAsync(follow => follow.CreatorId == creator.Id, cancellationToken);
        var isFollowing = viewer is { } traveler && await db.Follows.AnyAsync(follow => follow.CreatorId == creator.Id && follow.TravelerId == traveler, cancellationToken);
        var domain = creator.ToDomain();
        var connected = await db.ConnectedAccounts.AsNoTracking().Where(account => account.CreatorId == creator.Id).OrderBy(account => account.Platform).Select(account => account.Platform).ToListAsync(cancellationToken);

        return new CreatorPageDto(
            creator.Id,
            creator.Handle,
            creator.DisplayName,
            creator.Bio,
            creator.AvatarPath,
            creator.Languages,
            creator.Specialties,
            [.. domain.Profile.Links.Select(link => new CreatorLinkDto(link.Kind, link.Url))],
            followers >= FollowerDisplayThreshold ? followers : null,
            followers < FollowerDisplayThreshold,
            places.Count,
            rows.Select(row => row.DestinationId).Distinct().Count(),
            isFollowing,
            places,
            recent,
            connected);
    }

    public async Task<CreatorListDto> ListPublishedAsync(string? destinationSlug, string? specialty, int offset, int limit, CancellationToken cancellationToken)
    {
        var visible = Visible();
        if (destinationSlug is not null)
        {
            visible = visible.Where(row => row.DestinationSlug == destinationSlug);
        }

        var creators = db.Creators.AsNoTracking().Where(creator => creator.Status == CreatorStatuses.Published);
        if (specialty is not null)
        {
            creators = creators.Where(creator => creator.Specialties.Contains(specialty));
        }

        // The number of places is a correlated count over the visibility rule; with a destination only creators who have a place there are listed.
        var query = creators.Select(creator => new
        {
            creator.Id,
            creator.Handle,
            creator.DisplayName,
            creator.AvatarPath,
            creator.Specialties,
            Places = visible.Where(row => row.CreatorId == creator.Id).Select(row => row.PoiId).Distinct().Count(),
        });
        if (destinationSlug is not null)
        {
            query = query.Where(item => item.Places > 0);
        }

        var page = await query.OrderByDescending(item => item.Places).ThenBy(item => item.Handle).Skip(offset).Take(limit + 1).ToListAsync(cancellationToken);
        return new CreatorListDto(
            [.. page.Take(limit).Select(item => new CreatorSummaryDto(item.Id, item.Handle, item.DisplayName, item.AvatarPath, item.Specialties, item.Places))],
            page.Count > limit ? (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    public async Task<PoiCreatorsDto> ListForPoiAsync(Guid poiId, int limit, CancellationToken cancellationToken)
    {
        var rows = await Visible().Where(row => row.PoiId == poiId).ToListAsync(cancellationToken);
        var perCreator = rows.GroupBy(row => row.CreatorId)
            .Select(group => group.OrderByDescending(row => row.ValidatedAt).First())
            .OrderByDescending(row => row.ValidatedAt)
            .ToList();
        var selected = perCreator.Take(limit).ToList();
        var ids = selected.Select(row => row.CreatorId).ToList();
        var summaries = (await SummariesAsync(ids, cancellationToken)).ToDictionary(summary => summary.Id);
        var tips = await db.Tips.AsNoTracking().Where(tip => tip.PoiId == poiId && ids.Contains(tip.CreatorId) && tip.Status == TipStatuses.Published).ToDictionaryAsync(tip => tip.CreatorId, tip => tip.Text, cancellationToken);
        return new PoiCreatorsDto(poiId, perCreator.Count, [.. selected.Select(row => new PoiCreatorItemDto(summaries[row.CreatorId], tips.GetValueOrDefault(row.CreatorId), ToContent(row)))]);
    }

    public Task<bool> IsPublishedAsync(Guid creatorId, CancellationToken cancellationToken) =>
        db.Creators.AnyAsync(creator => creator.Id == creatorId && creator.Status == CreatorStatuses.Published, cancellationToken);

    public async Task<IReadOnlyList<FollowedCreatorDto>> ListFollowsAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var follows = await db.Follows.AsNoTracking().Where(follow => follow.TravelerId == travelerId)
            .Join(db.Creators.Where(creator => creator.Status == CreatorStatuses.Published), follow => follow.CreatorId, creator => creator.Id, (follow, creator) => new { follow.CreatorId, follow.FollowedAt })
            .OrderByDescending(item => item.FollowedAt).ToListAsync(cancellationToken);
        var summaries = (await SummariesAsync([.. follows.Select(item => item.CreatorId)], cancellationToken)).ToDictionary(summary => summary.Id);
        return [.. follows.Select(item => new FollowedCreatorDto(summaries[item.CreatorId], item.FollowedAt))];
    }

    private async Task<IReadOnlyList<CreatorSummaryDto>> SummariesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var creators = await db.Creators.AsNoTracking().Where(creator => ids.Contains(creator.Id)).ToListAsync(cancellationToken);
        var counts = await Visible().Where(row => ids.Contains(row.CreatorId)).GroupBy(row => row.CreatorId)
            .Select(group => new { CreatorId = group.Key, Places = group.Select(row => row.PoiId).Distinct().Count() }).ToDictionaryAsync(item => item.CreatorId, item => item.Places, cancellationToken);
        return [.. creators.Select(creator => new CreatorSummaryDto(creator.Id, creator.Handle, creator.DisplayName, creator.AvatarPath, creator.Specialties, counts.GetValueOrDefault(creator.Id)))];
    }

    public async Task<IReadOnlyList<AdminCreatorSummaryDto>> ListAdminAsync(string? status, string? search, int limit, CancellationToken cancellationToken)
    {
        var creators = db.Creators.AsNoTracking();
        if (status is not null)
        {
            creators = creators.Where(creator => creator.Status == status);
        }

        if (search is not null)
        {
            var pattern = "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            creators = creators.Where(creator => EF.Functions.ILike(creator.Handle, pattern) || EF.Functions.ILike(creator.DisplayName, pattern));
        }

        var rows = await creators.OrderBy(creator => creator.Handle).Take(limit).ToListAsync(cancellationToken);
        var ids = rows.Select(creator => creator.Id).ToList();
        var places = await db.PlaceLinks.AsNoTracking().Where(link => ids.Contains(link.CreatorId) && link.Status == PlaceLinkStatuses.Validated)
            .GroupBy(link => link.CreatorId).Select(group => new { CreatorId = group.Key, Count = group.Select(link => link.PoiId).Distinct().Count() })
            .ToDictionaryAsync(item => item.CreatorId, item => item.Count, cancellationToken);
        return [.. rows.Select(row =>
        {
            var creator = row.ToDomain();
            return new AdminCreatorSummaryDto(row.Id, row.Handle, row.DisplayName, row.Status, row.Founding, row.TermsVersion, row.Specialties.Length, places.GetValueOrDefault(row.Id), row.AccountId is not null, Block(creator));
        })];
    }

    public async Task<AdminCreatorDetailDto?> GetAdminDetailAsync(Guid creatorId, CancellationToken cancellationToken)
    {
        var row = await db.Creators.AsNoTracking().FirstOrDefaultAsync(creator => creator.Id == creatorId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var creator = row.ToDomain();
        var contents = await db.Contents.AsNoTracking().Where(content => content.CreatorId == creatorId).OrderByDescending(content => content.CreatedAt).ToListAsync(cancellationToken);
        var links = await db.PlaceLinks.AsNoTracking().Where(link => link.CreatorId == creatorId).OrderByDescending(link => link.CreatedAt).ToListAsync(cancellationToken);
        var tips = await db.Tips.AsNoTracking().Where(tip => tip.CreatorId == creatorId).OrderByDescending(tip => tip.UpdatedAt).ToListAsync(cancellationToken);
        var poiIds = links.Select(link => link.PoiId).Concat(tips.Select(tip => tip.PoiId)).Distinct().ToList();
        var names = await db.Pois.AsNoTracking().Where(poi => poiIds.Contains(poi.PoiId)).ToDictionaryAsync(poi => poi.PoiId, poi => poi.Name, cancellationToken);
        var titles = contents.ToDictionary(content => content.Id, content => content.Title);
        var followers = await db.Follows.CountAsync(follow => follow.CreatorId == creatorId, cancellationToken);

        return new AdminCreatorDetailDto(
            row.Id,
            row.AccountId,
            row.Handle,
            row.DisplayName,
            row.Bio,
            row.AvatarPath,
            row.Languages,
            row.Specialties,
            row.DestinationIds,
            [.. creator.Profile.Links.Select(link => new CreatorLinkDto(link.Kind, link.Url))],
            row.Status,
            row.Founding,
            row.TermsVersion,
            row.TermsDocumentRef,
            row.TermsAcceptedAt,
            followers,
            Block(creator),
            [.. contents.Select(content => new AdminContentDto(content.Id, content.Platform, content.Kind, content.Title, content.Permalink, content.CaptionExcerpt, content.CoverPath, content.DurationS, content.PublishedAt, content.IsCommercial, content.Status, [.. Mapping.Chapters(content.Chapters).Select(chapter => new ChapterDto(chapter.StartSeconds, chapter.Title))]))],
            [.. links.Select(link => new AdminPlaceLinkDto(link.Id, link.PoiId, names.GetValueOrDefault(link.PoiId, string.Empty), link.ContentId, link.ContentId is { } id ? titles.GetValueOrDefault(id) : null, link.StartS, link.Confidence, link.Status, link.ValidatedAt))],
            [.. tips.Select(tip => new AdminTipDto(tip.Id, tip.PoiId, names.GetValueOrDefault(tip.PoiId, string.Empty), tip.Text, tip.Status, tip.UpdatedAt))]);
    }

    public async Task<PlaceProposalsDto> ListProposalsAsync(Guid creatorId, double bulkThreshold, CancellationToken cancellationToken)
    {
        var rows = await (
            from link in db.PlaceLinks.AsNoTracking()
            where link.CreatorId == creatorId && link.Status == PlaceLinkStatuses.Proposed
            join poi in db.Pois.AsNoTracking() on link.PoiId equals poi.PoiId
            join content in db.Contents.AsNoTracking() on link.ContentId equals content.Id into contents
            from content in contents.DefaultIfEmpty()
            select new { link.Id, link.PoiId, PoiName = poi.Name, poi.City, poi.DestinationSlug, ContentId = (Guid?)content.Id, content.Title, content.Platform, content.Permalink, link.StartS, link.Confidence, link.Signals }).ToListAsync(cancellationToken);
        var pending = await db.Contents.AsNoTracking().CountAsync(content => content.CreatorId == creatorId && content.Status == ContentStatuses.Imported && content.GeotaggedAt == null, cancellationToken);

        var items = rows.Select(row =>
        {
            var (signals, evidence) = ReadSignals(row.Signals);
            return (row.DestinationSlug, Item: new PlaceProposalDto(
                row.Id, row.PoiId, row.PoiName, row.City, row.DestinationSlug, row.ContentId, row.Title,
                row.Permalink is null ? null : ContentUrls.At(row.Platform!, row.Permalink, row.StartS), row.StartS, Math.Round(row.Confidence, 3), evidence, signals));
        }).ToList();
        var groups = items.GroupBy(item => item.DestinationSlug)
            .Select(group => new PlaceProposalGroupDto(group.Key, [.. group.Select(item => item.Item).OrderByDescending(item => item.Confidence).ThenBy(item => item.PoiName, StringComparer.CurrentCultureIgnoreCase)]))
            .OrderByDescending(group => group.Items.Count).ThenBy(group => group.Destination, StringComparer.Ordinal).ToList();
        return new PlaceProposalsDto(items.Count, items.Count(item => item.Item.Confidence >= bulkThreshold), bulkThreshold, pending, groups);
    }

    private static (IReadOnlyList<string> Signals, string? Evidence) ReadSignals(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var signals = document.RootElement.TryGetProperty("signals", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToList() : [];
            return (signals, document.RootElement.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.String ? evidence.GetString() : null);
        }
        catch (JsonException)
        {
            return ([], null);
        }
    }

    private static PublishBlockDto? Block(Creator creator) => creator.PublishBlock is { } block ? new PublishBlockDto(block.Code, block.Message) : null;

    public async Task<IReadOnlyList<ModerationCaseDto>> ListModerationAsync(string? status, int limit, CancellationToken cancellationToken)
    {
        var cases = db.Cases.AsNoTracking();
        if (status is not null)
        {
            cases = cases.Where(item => item.Status == status);
        }

        // The queue: open cases first, oldest first; decided ones follow, most recent first.
        var rows = await cases.OrderBy(item => item.Status == ModerationStatuses.Open ? 0 : 1)
            .ThenBy(item => item.Status == ModerationStatuses.Open ? item.CreatedAt : DateTimeOffset.MaxValue)
            .ThenByDescending(item => item.DecidedAt)
            .Take(limit).ToListAsync(cancellationToken);
        return await DescribeAsync(rows, cancellationToken);
    }

    public async Task<ModerationCaseDto?> GetModerationAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var row = await db.Cases.AsNoTracking().FirstOrDefaultAsync(item => item.Id == caseId, cancellationToken);
        return row is null ? null : (await DescribeAsync([row], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<ModerationCaseDto>> DescribeAsync(IReadOnlyList<ModerationCaseRow> rows, CancellationToken cancellationToken)
    {
        // Resolve what each case is about with one query per kind of target. A target deleted since is labelled as such.
        Guid[] Ids(string type) => [.. rows.Where(item => item.TargetType == type).Select(item => item.TargetId)];
        var creatorIds = Ids(ModerationTargets.Creator);
        var wantedContents = Ids(ModerationTargets.Content);
        var wantedLinks = Ids(ModerationTargets.PlaceLink);
        var wantedTips = Ids(ModerationTargets.Tip);
        var contentRows = await db.Contents.AsNoTracking().Where(content => wantedContents.Contains(content.Id)).ToDictionaryAsync(content => content.Id, cancellationToken);
        var linkRows = await db.PlaceLinks.AsNoTracking().Where(link => wantedLinks.Contains(link.Id)).ToDictionaryAsync(link => link.Id, cancellationToken);
        var tipRows = await db.Tips.AsNoTracking().Where(tip => wantedTips.Contains(tip.Id)).ToDictionaryAsync(tip => tip.Id, cancellationToken);
        var wantedCreators = creatorIds.Concat(contentRows.Values.Select(content => content.CreatorId)).Concat(linkRows.Values.Select(link => link.CreatorId)).Concat(tipRows.Values.Select(tip => tip.CreatorId)).Distinct().ToList();
        var handles = await db.Creators.AsNoTracking().Where(creator => wantedCreators.Contains(creator.Id)).ToDictionaryAsync(creator => creator.Id, creator => creator.Handle, cancellationToken);
        var poiIds = linkRows.Values.Select(link => link.PoiId).ToList();
        var poiNames = await db.Pois.AsNoTracking().Where(poi => poiIds.Contains(poi.PoiId)).ToDictionaryAsync(poi => poi.PoiId, poi => poi.Name, cancellationToken);

        return [.. rows.Select(row =>
        {
            Guid? creatorId = null;
            var label = "(supprimé)";
            switch (row.TargetType)
            {
                case ModerationTargets.Creator:
                    creatorId = row.TargetId;
                    label = handles.TryGetValue(row.TargetId, out var handle) ? $"Profil @{handle}" : label;
                    break;
                case ModerationTargets.Content when contentRows.TryGetValue(row.TargetId, out var content):
                    creatorId = content.CreatorId;
                    label = $"Contenu « {content.Title} »";
                    break;
                case ModerationTargets.PlaceLink when linkRows.TryGetValue(row.TargetId, out var link):
                    creatorId = link.CreatorId;
                    label = $"Association avec {poiNames.GetValueOrDefault(link.PoiId, "un lieu")}";
                    break;
                case ModerationTargets.Tip when tipRows.TryGetValue(row.TargetId, out var tip):
                    creatorId = tip.CreatorId;
                    label = $"Conseil « {(tip.Text.Length > 80 ? tip.Text[..80] + "…" : tip.Text)} »";
                    break;
            }

            return new ModerationCaseDto(row.Id, row.TargetType, row.TargetId, label, creatorId, creatorId is { } id ? handles.GetValueOrDefault(id) : null, row.Reason, row.Status, row.Decision, row.StatementOfReasons, row.CreatedAt, row.DecidedAt);
        })];
    }
}
