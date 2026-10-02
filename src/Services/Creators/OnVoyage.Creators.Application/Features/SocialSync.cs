using Microsoft.Extensions.Logging;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;
using Wolverine;

namespace OnVoyage.Creators.Application.Features;

/// <summary>
/// Imports the contents of a connected account (F-27): the most recent ones at first, then incrementally every day or on request. Only references are
/// kept (D-17). A content that disappeared from the platform is withdrawn on the next run, with its places and its thumbnail. The creator's own
/// choices survive a run: a hidden content stays hidden, a removed one is not brought back, the « Publicité » label is only ever added.
/// </summary>
public static class SocialSyncHandler
{
    /// <summary>The 200 most recent contents of an account (F-27).</summary>
    public const int ImportLimit = 200;

    public static async Task<Result<SyncReportDto>> Handle(SyncConnectedAccountCommand command, IConnectedAccountRepository accounts, IContentRepository contents, ISocialConnector connector, IThumbnailStore thumbnails, ICreatorsUnitOfWork unit, IMessageBus bus, TimeProvider clock, ILogger<SyncConnectedAccountCommand> logger, CancellationToken cancellationToken)
    {
        if (await accounts.FindAsync(command.ConnectedAccountId, cancellationToken) is not { IsActive: true } account)
        {
            return Result.Failure<SyncReportDto>("connection_not_found", "Compte connecté introuvable ou à reconnecter.");
        }

        var now = clock.GetUtcNow();
        var fetch = await connector.FetchAsync(account.Id, ImportLimit, cancellationToken);
        account = await accounts.FindAsync(account.Id, cancellationToken) ?? account; // the connector may have renewed the tokens: keep their new expiry
        if (fetch.Status == FetchStatus.NeedsReauth)
        {
            await accounts.StageAsync(account.NeedingReauth(fetch.Error ?? "reauthorization_required"), cancellationToken);
            await unit.CommitAsync([], cancellationToken);
            logger.LogWarning("Connected account {AccountId} ({Platform}) needs to be authorized again.", account.Id, account.Platform);
            return Result.Success(new SyncReportDto(0, 0, 0, ConnectionStatuses.NeedsReauth));
        }

        if (fetch.Status != FetchStatus.Ok)
        {
            await accounts.StageAsync(account.Failed(fetch.Error ?? "unavailable"), cancellationToken);
            await unit.CommitAsync([], cancellationToken);
            return Result.Failure<SyncReportDto>("provider_error", "La plateforme n'a pas répondu : la prochaine synchronisation réessaiera.");
        }

        List<object> events = [];
        int created = 0, updated = 0, removed = 0;
        List<Guid> toAnalyze = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var remote in fetch.Items)
        {
            var externalId = ExternalId(account.Platform, remote);
            if (!seen.Add(externalId))
            {
                continue;
            }

            var existing = await contents.FindContentAsync(account.Platform, externalId, cancellationToken);
            if (existing is not null && existing.CreatorId != account.CreatorId)
            {
                continue; // somebody else referenced this content by its URL: it is not ours to take over
            }

            var title = Clip(remote.Title, ContentItem.MaxTitle) is { Length: > 0 } text ? text : "Sans titre";
            var excerpt = remote.Caption is { Length: > 0 } caption ? Clip(caption, ContentItem.MaxCaptionExcerpt) : null;
            var chapters = ContentRules.NormalizeChapters(remote.Chapters, remote.DurationSeconds).Chapters ?? [];
            var commercial = CommercialDisclosure.Detects(remote.Title, remote.Caption);
            var kind = ContentKinds.All.Contains(remote.Kind) ? remote.Kind : ContentKinds.Video;

            if (existing is null)
            {
                var id = Guid.CreateVersion7();
                var cover = remote.ThumbnailUrl is { } url ? await thumbnails.SaveAsync(account.CreatorId, id, url, cancellationToken) : null;
                await contents.StageContentAsync(new ContentItem(id, account.CreatorId, account.Platform, externalId, remote.Permalink, title, excerpt, remote.PublishedAt, remote.DurationSeconds, kind, cover, chapters, commercial, ContentStatuses.Imported, account.Id), cancellationToken);
                toAnalyze.Add(id);
                created++;
                continue;
            }

            if (existing.Status == ContentStatuses.Removed)
            {
                continue;
            }

            var cover2 = existing.CoverPath;
            if (cover2 is null && remote.ThumbnailUrl is { } thumbnail)
            {
                cover2 = await thumbnails.SaveAsync(account.CreatorId, existing.Id, thumbnail, cancellationToken);
            }

            var refreshed = existing with
            {
                Title = title,
                CaptionExcerpt = excerpt,
                Permalink = remote.Permalink,
                DurationSeconds = remote.DurationSeconds,
                Chapters = chapters,
                CoverPath = cover2,
                ConnectedAccountId = account.Id,
                IsCommercial = existing.IsCommercial || commercial,
            };
            if (!SameContent(refreshed, existing))
            {
                await contents.StageContentAsync(refreshed, cancellationToken);
                updated++;
                if (existing.IsOnline && refreshed.IsCommercial != existing.IsCommercial)
                {
                    events.AddRange((await contents.ListLinksOfContentAsync(existing.Id, cancellationToken)).Where(link => link.IsValidated).Select(link => CreatorEvents.LinkChanged(link, refreshed, true, now)));
                }
            }
        }

        // What the platform no longer lists is gone from it. When the list reached its end, everything missing is; otherwise only what is
        // newer than the oldest listed content (older ones may simply be past the limit).
        // An empty answer never withdraws anything: an account with nothing to list and a platform that glitched look the same.
        var oldest = fetch.Items.Select(item => item.PublishedAt).Where(date => date is not null).Min();
        foreach (var stored in await contents.ListContentsOfAccountAsync(account.Id, cancellationToken))
        {
            if (fetch.Items.Count == 0 || stored.Status == ContentStatuses.Removed || seen.Contains(stored.ExternalId)
                || !(fetch.Complete || stored.PublishedAt is { } published && oldest is { } floor && published >= floor))
            {
                continue;
            }

            var gone = stored with { Status = ContentStatuses.Removed, CoverPath = ThumbnailPaths.IsOurs(stored.CoverPath) ? null : stored.CoverPath };
            if (ThumbnailPaths.IsOurs(stored.CoverPath))
            {
                await thumbnails.DeleteAsync(stored.CoverPath!, cancellationToken);
            }

            await contents.StageContentAsync(gone, cancellationToken);
            if (stored.IsOnline)
            {
                events.AddRange((await contents.ListLinksOfContentAsync(stored.Id, cancellationToken)).Where(link => link.IsValidated).Select(link => CreatorEvents.LinkChanged(link, gone, false, now)));
            }

            removed++;
        }

        await accounts.StageAsync(account.Synced(now), cancellationToken);
        await unit.CommitAsync(events, cancellationToken);

        // New contents go to the assistant that looks for the places they talk about (F-28): proposals only, in the background.
        foreach (var contentId in toAnalyze)
        {
            await bus.PublishAsync(new AnalyzeContentCommand(contentId));
        }

        logger.LogInformation("Synchronised {Platform} account {AccountId}: {Created} created, {Updated} updated, {Removed} removed.", account.Platform, account.Id, created, updated, removed);
        return Result.Success(new SyncReportDto(created, updated, removed, ConnectionStatuses.Active));
    }

    /// <summary>The identifier a content is stored under. An Instagram post is known by its short code everywhere else (URL typed by hand), so the import uses it too: one post, one row.</summary>
    internal static string ExternalId(string platform, RemoteContent remote) =>
        platform == ContentPlatforms.Instagram && ContentUrls.Parse(remote.Permalink) is { Platform: ContentPlatforms.Instagram } parsed ? parsed.ExternalId : remote.ExternalId;

    private static bool SameContent(ContentItem a, ContentItem b) =>
        a.Title == b.Title && a.CaptionExcerpt == b.CaptionExcerpt && a.Permalink == b.Permalink && a.DurationSeconds == b.DurationSeconds && a.CoverPath == b.CoverPath
        && a.IsCommercial == b.IsCommercial && a.ConnectedAccountId == b.ConnectedAccountId && a.Chapters.SequenceEqual(b.Chapters);

    private static string Clip(string text, int max) => text.Trim() is { } trimmed && trimmed.Length > max ? trimmed[..max].TrimEnd() : text.Trim();
}
