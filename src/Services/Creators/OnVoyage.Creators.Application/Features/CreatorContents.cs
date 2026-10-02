using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Features;

public sealed record AddContentCommand(Guid Actor, Guid CreatorId, AddContentRequest Content);

public sealed record UpdateContentCommand(Guid Actor, Guid CreatorId, Guid ContentId, UpdateContentRequest Content);

public sealed record RemoveContentCommand(Guid Actor, Guid CreatorId, Guid ContentId);

public sealed record AddPlaceLinkCommand(Guid Actor, Guid CreatorId, AddPlaceLinkRequest Link);

public sealed record SetPlaceLinkStatusCommand(Guid Actor, Guid CreatorId, Guid LinkId, string Status);

public sealed record RemovePlaceLinkCommand(Guid Actor, Guid CreatorId, Guid LinkId);

public sealed record SetTipCommand(Guid Actor, Guid CreatorId, Guid PoiId, string Text);

public sealed record RemoveTipCommand(Guid Actor, Guid CreatorId, Guid PoiId);

public sealed record SearchPlacesQuery(string Query, string? Destination, int Limit);

/// <summary>
/// Contents referenced by URL, their associations with places, and the creators' tips (F-26, F-28). A content stays on its platform; only the
/// reference is kept. Only <c>validated</c> links are published: each change of that set is announced with <see cref="CreatorPlaceLinkChangedV1"/>.
/// </summary>
public static class CreatorContentHandler
{
    public static async Task<Result<AdminContentDto>> Handle(AddContentCommand command, ICreatorRepository creators, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindAsync(command.CreatorId, cancellationToken) is null)
        {
            return CreatorNotFound<AdminContentDto>();
        }

        var request = command.Content;
        if (ContentUrls.Parse(request.Url) is not { } url)
        {
            return Result.Failure<AdminContentDto>("invalid_content_url", "L'adresse n'est pas celle d'une vidéo ou d'une publication publique YouTube, Instagram ou TikTok.");
        }

        var kind = string.IsNullOrWhiteSpace(request.Kind) ? url.DefaultKind : request.Kind.Trim().ToLowerInvariant();
        var excerpt = string.IsNullOrWhiteSpace(request.CaptionExcerpt) ? null : request.CaptionExcerpt.Trim();
        var cover = string.IsNullOrWhiteSpace(request.CoverPath) ? null : request.CoverPath.Trim();
        if (!ContentKinds.All.Contains(kind))
        {
            return Result.Failure<AdminContentDto>("validation", "Type de contenu inconnu (video, photo, carousel, article).");
        }

        if (ContentRules.CheckText(request.Title, excerpt, cover, request.DurationSeconds) is { } violation)
        {
            return Result.Failure<AdminContentDto>(violation);
        }

        var (chapters, chapterViolation) = ContentRules.NormalizeChapters(request.Chapters?.Select(chapter => new Chapter(chapter.StartSeconds, chapter.Title)), request.DurationSeconds);
        if (chapters is null)
        {
            return Result.Failure<AdminContentDto>(chapterViolation!);
        }

        if (await contents.ContentExistsAsync(url.Platform, url.ExternalId, cancellationToken))
        {
            return Exists();
        }

        var now = clock.GetUtcNow();
        var content = new ContentItem(Guid.CreateVersion7(), command.CreatorId, url.Platform, url.ExternalId, url.Permalink, request.Title.Trim(), excerpt, request.PublishedAt, request.DurationSeconds, kind, cover, chapters, request.IsCommercial, ContentStatuses.Imported);
        await contents.StageContentAsync(content, cancellationToken);
        try
        {
            await unit.CommitAsync([CreatorEvents.Audit(command.Actor, "content.add", $"creator:{command.CreatorId}", $"{content.Platform}:{content.ExternalId}", now)], cancellationToken);
        }
        catch (UniqueConflictException)
        {
            return Exists();
        }

        return Result.Success(ToDto(content));
    }

    public static async Task<Result<AdminContentDto>> Handle(UpdateContentCommand command, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await contents.FindContentAsync(command.ContentId, cancellationToken) is not { } content || content.CreatorId != command.CreatorId)
        {
            return Result.Failure<AdminContentDto>("content_not_found", "Contenu introuvable.");
        }

        var request = command.Content;
        if (content.Status == ContentStatuses.Removed)
        {
            return Result.Failure<AdminContentDto>("content_removed", "Ce contenu a été retiré.");
        }

        if (request.Status is not (ContentStatuses.Imported or ContentStatuses.Hidden))
        {
            return Result.Failure<AdminContentDto>("validation", "Le statut est « imported » (en ligne) ou « hidden » (masqué).");
        }

        var excerpt = string.IsNullOrWhiteSpace(request.CaptionExcerpt) ? null : request.CaptionExcerpt.Trim();
        var cover = string.IsNullOrWhiteSpace(request.CoverPath) ? null : request.CoverPath.Trim();
        if (ContentRules.CheckText(request.Title, excerpt, cover, request.DurationSeconds) is { } violation)
        {
            return Result.Failure<AdminContentDto>(violation);
        }

        var (chapters, chapterViolation) = ContentRules.NormalizeChapters(request.Chapters?.Select(chapter => new Chapter(chapter.StartSeconds, chapter.Title)), request.DurationSeconds);
        if (chapters is null)
        {
            return Result.Failure<AdminContentDto>(chapterViolation!);
        }

        var now = clock.GetUtcNow();
        var updated = content with { Title = request.Title.Trim(), CaptionExcerpt = excerpt, CoverPath = cover, DurationSeconds = request.DurationSeconds, IsCommercial = request.IsCommercial, Chapters = chapters, Status = request.Status };
        await contents.StageContentAsync(updated, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "content.update", $"creator:{command.CreatorId}", $"content={content.Id}; status={updated.Status}", now)];

        // Going offline withdraws the places this content supported; coming back online restores them. A changed commercial flag is announced too.
        if (content.IsOnline != updated.IsOnline || content.IsCommercial != updated.IsCommercial)
        {
            foreach (var link in (await contents.ListLinksOfContentAsync(content.Id, cancellationToken)).Where(link => link.IsValidated))
            {
                events.Add(CreatorEvents.LinkChanged(link, updated, updated.IsOnline, now));
            }
        }

        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(ToDto(updated));
    }

    public static async Task<Result<bool>> Handle(RemoveContentCommand command, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await contents.FindContentAsync(command.ContentId, cancellationToken) is not { } content || content.CreatorId != command.CreatorId)
        {
            return Result.Failure<bool>("content_not_found", "Contenu introuvable.");
        }

        if (content.Status == ContentStatuses.Removed)
        {
            return Result.Success(true);
        }

        var now = clock.GetUtcNow();
        var removed = content with { Status = ContentStatuses.Removed };
        await contents.StageContentAsync(removed, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "content.remove", $"creator:{command.CreatorId}", $"content={content.Id}", now)];
        if (content.IsOnline)
        {
            events.AddRange((await contents.ListLinksOfContentAsync(content.Id, cancellationToken)).Where(link => link.IsValidated).Select(link => CreatorEvents.LinkChanged(link, removed, false, now)));
        }

        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<AdminPlaceLinkDto>> Handle(AddPlaceLinkCommand command, ICreatorRepository creators, IContentRepository contents, IPoiDirectory directory, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindAsync(command.CreatorId, cancellationToken) is null)
        {
            return CreatorNotFound<AdminPlaceLinkDto>();
        }

        var request = command.Link;
        var status = string.IsNullOrWhiteSpace(request.Status) ? PlaceLinkStatuses.Validated : request.Status.Trim();
        if (!PlaceLinkStatuses.IsKnown(status))
        {
            return Result.Failure<AdminPlaceLinkDto>("validation", "Le statut est « proposed », « validated » ou « rejected ».");
        }

        if (request.StartSeconds is < 0 || request.StartSeconds is not null && request.ContentId is null)
        {
            return Result.Failure<AdminPlaceLinkDto>("validation", "Un horodatage suppose un contenu, et il ne peut pas être négatif.");
        }

        if (await directory.FindAsync(request.PoiId, cancellationToken) is not { } place)
        {
            return Result.Failure<AdminPlaceLinkDto>("place_not_found", "Lieu inconnu du répertoire.");
        }

        ContentItem? content = null;
        if (request.ContentId is { } contentId)
        {
            content = await contents.FindContentAsync(contentId, cancellationToken);
            if (content is null || content.CreatorId != command.CreatorId || content.Status == ContentStatuses.Removed)
            {
                return Result.Failure<AdminPlaceLinkDto>("content_not_found", "Contenu introuvable chez ce créateur.");
            }

            if (request.StartSeconds is { } start && content.DurationSeconds is { } duration && start > duration)
            {
                return Result.Failure<AdminPlaceLinkDto>("validation", "L'horodatage dépasse la durée du contenu.");
            }
        }

        var now = clock.GetUtcNow();
        var existing = await contents.FindLinkAsync(command.CreatorId, request.PoiId, request.ContentId, request.StartSeconds, cancellationToken);
        var link = existing?.WithStatus(status, now) ?? new PlaceLink(Guid.CreateVersion7(), command.CreatorId, request.PoiId, request.ContentId, request.StartSeconds, 1d, status, null, now, "{\"source\":\"admin\"}").WithStatus(status, now);
        await contents.StageLinkAsync(link, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "place_link.add", $"creator:{command.CreatorId}", $"poi={request.PoiId}; content={request.ContentId}; status={status}", now)];
        events.AddRange(StatusChange(existing, link, content, now));
        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(new AdminPlaceLinkDto(link.Id, link.PoiId, place.DisplayName, link.ContentId, content?.Title, link.StartSeconds, link.Confidence, link.Status, link.ValidatedAt));
    }

    public static async Task<Result<AdminPlaceLinkDto>> Handle(SetPlaceLinkStatusCommand command, IContentRepository contents, IPoiDirectory directory, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!PlaceLinkStatuses.IsKnown(command.Status))
        {
            return Result.Failure<AdminPlaceLinkDto>("validation", "Le statut est « proposed », « validated » ou « rejected ».");
        }

        if (await contents.FindLinkAsync(command.LinkId, cancellationToken) is not { } existing || existing.CreatorId != command.CreatorId)
        {
            return Result.Failure<AdminPlaceLinkDto>("place_link_not_found", "Association introuvable.");
        }

        var now = clock.GetUtcNow();
        var content = existing.ContentId is { } id ? await contents.FindContentAsync(id, cancellationToken) : null;
        var link = existing.WithStatus(command.Status, now);
        await contents.StageLinkAsync(link, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "place_link.status", $"creator:{command.CreatorId}", $"link={link.Id}; status={link.Status}", now)];
        events.AddRange(StatusChange(existing, link, content, now));
        await unit.CommitAsync(events, cancellationToken);
        var place = await directory.FindAsync(link.PoiId, cancellationToken);
        return Result.Success(new AdminPlaceLinkDto(link.Id, link.PoiId, place?.DisplayName ?? string.Empty, link.ContentId, content?.Title, link.StartSeconds, link.Confidence, link.Status, link.ValidatedAt));
    }

    public static async Task<Result<bool>> Handle(RemovePlaceLinkCommand command, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await contents.FindLinkAsync(command.LinkId, cancellationToken) is not { } link || link.CreatorId != command.CreatorId)
        {
            return Result.Failure<bool>("place_link_not_found", "Association introuvable.");
        }

        var now = clock.GetUtcNow();
        var content = link.ContentId is { } id ? await contents.FindContentAsync(id, cancellationToken) : null;
        await contents.DeleteLinkAsync(link.Id, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "place_link.remove", $"creator:{command.CreatorId}", $"link={link.Id}", now)];
        events.AddRange(StatusChange(link, null, content, now));
        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(true);
    }

    /// <summary>
    /// Sets the tip of a creator for a place. A tip with no content is a recommendation on its own ("conseil seul"): when the creator has no
    /// association with the place yet, a validated one without content is created, so the tip can be shown.
    /// </summary>
    public static async Task<Result<AdminTipDto>> Handle(SetTipCommand command, ICreatorRepository creators, IContentRepository contents, IPoiDirectory directory, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindAsync(command.CreatorId, cancellationToken) is null)
        {
            return CreatorNotFound<AdminTipDto>();
        }

        if (CreatorTip.Check(command.Text) is { } violation)
        {
            return Result.Failure<AdminTipDto>(violation);
        }

        if (await directory.FindAsync(command.PoiId, cancellationToken) is not { } place)
        {
            return Result.Failure<AdminTipDto>("place_not_found", "Lieu inconnu du répertoire.");
        }

        var now = clock.GetUtcNow();
        var existing = await contents.FindTipAsync(command.CreatorId, command.PoiId, cancellationToken);
        var tip = new CreatorTip(existing?.Id ?? Guid.CreateVersion7(), command.CreatorId, command.PoiId, command.Text.Trim(), now, TipStatuses.Published);
        await contents.StageTipAsync(tip, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "tip.set", $"creator:{command.CreatorId}", $"poi={command.PoiId}", now)];
        if (!(await contents.ListLinksAsync(command.CreatorId, cancellationToken)).Any(link => link.PoiId == command.PoiId))
        {
            var link = new PlaceLink(Guid.CreateVersion7(), command.CreatorId, command.PoiId, null, null, 1d, PlaceLinkStatuses.Validated, now, now, "{\"source\":\"tip\"}");
            await contents.StageLinkAsync(link, cancellationToken);
            events.Add(CreatorEvents.LinkChanged(link, null, true, now));
        }

        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(new AdminTipDto(tip.Id, tip.PoiId, place.DisplayName, tip.Text, tip.Status, tip.UpdatedAt));
    }

    public static async Task<Result<bool>> Handle(RemoveTipCommand command, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await contents.FindTipAsync(command.CreatorId, command.PoiId, cancellationToken) is null)
        {
            return Result.Failure<bool>("tip_not_found", "Conseil introuvable.");
        }

        await contents.DeleteTipAsync(command.CreatorId, command.PoiId, cancellationToken);
        await unit.CommitAsync([CreatorEvents.Audit(command.Actor, "tip.remove", $"creator:{command.CreatorId}", $"poi={command.PoiId}", clock.GetUtcNow())], cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<IReadOnlyList<PoiSearchResultDto>>> Handle(SearchPlacesQuery query, IPoiDirectory directory, CancellationToken cancellationToken)
    {
        var text = PoiEntry.Fold(query.Query);
        return text.Length < 2
            ? Result.Failure<IReadOnlyList<PoiSearchResultDto>>("validation", "Saisissez au moins deux caractères.")
            : Result.Success(await directory.SearchAsync(text, string.IsNullOrWhiteSpace(query.Destination) ? null : query.Destination.Trim(), Math.Clamp(query.Limit, 1, 50), cancellationToken));
    }

    /// <summary>The published set changed: a link that became validated (and online) is announced, one that stopped being so is withdrawn.</summary>
    internal static IEnumerable<CreatorPlaceLinkChangedV1> StatusChange(PlaceLink? before, PlaceLink? after, ContentItem? content, DateTimeOffset now)
    {
        var online = content is null || content.IsOnline;
        var wasPublic = before is { IsValidated: true } && online;
        var isPublic = after is { IsValidated: true } && online;
        if (isPublic && !wasPublic)
        {
            yield return CreatorEvents.LinkChanged(after!, content, true, now);
        }
        else if (!isPublic && wasPublic)
        {
            yield return CreatorEvents.LinkChanged(before!, content, false, now);
        }
    }

    private static AdminContentDto ToDto(ContentItem content) =>
        new(content.Id, content.Platform, content.Kind, content.Title, content.Permalink, content.CaptionExcerpt, content.CoverPath, content.DurationSeconds, content.PublishedAt, content.IsCommercial, content.Status,
            [.. content.Chapters.Select(chapter => new ChapterDto(chapter.StartSeconds, chapter.Title))]);

    private static Result<T> CreatorNotFound<T>() => Result.Failure<T>("creator_not_found", "Créateur introuvable.");

    private static Result<AdminContentDto> Exists() => Result.Failure<AdminContentDto>("content_exists", "Ce contenu est déjà référencé.");
}
