using System.Text.Json;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Infrastructure.Persistence;

/// <summary>The only place where rows and domain objects meet (strict Domain ↔ Infrastructure mapping).</summary>
internal static class Mapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Creator ToDomain(this CreatorRow row) =>
        new(
            row.Id,
            row.AccountId,
            new CreatorProfile(
                row.Handle,
                row.DisplayName,
                row.Bio,
                row.AvatarPath,
                row.Languages,
                row.Specialties,
                row.DestinationIds,
                [.. (JsonSerializer.Deserialize<List<LinkJson>>(row.Links, Json) ?? []).Select(link => new CreatorLink(link.Kind, link.Url))]),
            row.Status,
            row.Founding,
            row.TermsVersion,
            row.TermsDocumentRef,
            row.TermsAcceptedAt,
            row.CreatedAt,
            row.UpdatedAt);

    public static void CopyTo(this Creator creator, CreatorRow row)
    {
        row.AccountId = creator.AccountId;
        row.Handle = creator.Handle;
        row.TermsDocumentRef = creator.TermsDocumentRef;
        row.DisplayName = creator.Profile.DisplayName;
        row.Bio = creator.Profile.Bio;
        row.AvatarPath = creator.Profile.AvatarPath;
        row.Languages = [.. creator.Profile.Languages];
        row.Specialties = [.. creator.Profile.Specialties];
        row.DestinationIds = [.. creator.Profile.DestinationIds];
        row.Links = JsonSerializer.Serialize(creator.Profile.Links.Select(link => new LinkJson(link.Kind, link.Url)), Json);
        row.Status = creator.Status;
        row.TermsVersion = creator.TermsVersion;
        row.TermsAcceptedAt = creator.TermsAcceptedAt;
        row.Founding = creator.Founding;
        row.CreatedAt = creator.CreatedAt;
        row.UpdatedAt = creator.UpdatedAt;
    }

    public static ContentItem ToDomain(this ContentRow row) =>
        new(
            row.Id,
            row.CreatorId,
            row.Platform,
            row.ExternalId,
            row.Permalink,
            row.Title,
            row.CaptionExcerpt,
            row.PublishedAt,
            row.DurationS,
            row.Kind,
            row.CoverPath,
            Chapters(row.Chapters),
            row.IsCommercial,
            row.Status,
            row.ConnectedAccountId);

    public static ConnectedAccount ToDomain(this ConnectedAccountRow row) =>
        new(row.Id, row.CreatorId, row.Platform, row.ExternalUserId, row.Username, row.ExpiresAt, row.Scopes, row.LastSyncAt, row.Status, row.LastError, row.CreatedAt);

    /// <summary>Copies what is not secret. The tokens are never touched here.</summary>
    public static void CopyTo(this ConnectedAccount account, ConnectedAccountRow row)
    {
        row.CreatorId = account.CreatorId;
        row.Platform = account.Platform;
        row.ExternalUserId = account.ExternalUserId;
        row.Username = account.Username;
        row.ExpiresAt = account.ExpiresAt;
        row.Scopes = [.. account.Scopes];
        row.LastSyncAt = account.LastSyncAt;
        row.Status = account.Status;
        row.LastError = account.LastError;
        row.CreatedAt = account.CreatedAt;
        if (account.Status == ConnectionStatuses.NeedsReauth)
        {
            row.AccessTokenProtected = null;
            row.RefreshTokenProtected = null;
        }
    }

    public static IReadOnlyList<Chapter> Chapters(string json) =>
        [.. (JsonSerializer.Deserialize<List<ChapterJson>>(json, Json) ?? []).Select(chapter => new Chapter(chapter.StartSeconds, chapter.Title))];

    public static void CopyTo(this ContentItem content, ContentRow row, DateTimeOffset now)
    {
        row.CreatorId = content.CreatorId;
        row.Platform = content.Platform;
        row.ExternalId = content.ExternalId;
        row.Permalink = content.Permalink;
        row.Title = content.Title;
        row.CaptionExcerpt = content.CaptionExcerpt;
        row.PublishedAt = content.PublishedAt;
        row.DurationS = content.DurationSeconds;
        row.Kind = content.Kind;
        row.CoverPath = content.CoverPath;
        row.Chapters = JsonSerializer.Serialize(content.Chapters.Select(chapter => new ChapterJson(chapter.StartSeconds, chapter.Title)), Json);
        row.IsCommercial = content.IsCommercial;
        row.Status = content.Status;
        row.ConnectedAccountId = content.ConnectedAccountId;
        if (row.CreatedAt == default)
        {
            row.CreatedAt = now;
        }
    }

    public static PlaceLink ToDomain(this PlaceLinkRow row) =>
        new(row.Id, row.CreatorId, row.PoiId, row.ContentId, row.StartS, row.Confidence, row.Status, row.ValidatedAt, row.CreatedAt, row.Signals);

    public static void CopyTo(this PlaceLink link, PlaceLinkRow row)
    {
        row.CreatorId = link.CreatorId;
        row.PoiId = link.PoiId;
        row.ContentId = link.ContentId;
        row.StartS = link.StartSeconds;
        row.Confidence = (float)link.Confidence;
        row.Status = link.Status;
        row.ValidatedAt = link.ValidatedAt;
        row.CreatedAt = link.CreatedAt;
        row.Signals = link.Signals;
    }

    public static CreatorTip ToDomain(this TipRow row) => new(row.Id, row.CreatorId, row.PoiId, row.Text, row.UpdatedAt, row.Status);

    public static ModerationCase ToDomain(this ModerationCaseRow row) =>
        new(row.Id, row.TargetType, row.TargetId, row.Reason, row.ReporterRef, row.Status, row.Decision, row.StatementOfReasons, row.CreatedAt, row.DecidedAt);

    public static void CopyTo(this ModerationCase moderationCase, ModerationCaseRow row)
    {
        row.TargetType = moderationCase.TargetType;
        row.TargetId = moderationCase.TargetId;
        row.Reason = moderationCase.Reason;
        row.ReporterRef = moderationCase.ReporterRef;
        row.Status = moderationCase.Status;
        row.Decision = moderationCase.Decision;
        row.StatementOfReasons = moderationCase.StatementOfReasons;
        row.CreatedAt = moderationCase.CreatedAt;
        row.DecidedAt = moderationCase.DecidedAt;
    }

    public static PoiEntry ToDomain(this PoiDirectoryRow row)
    {
        var names = JsonSerializer.Deserialize<NamesJson>(row.Names, Json) ?? new NamesJson(row.Name, null, []);
        return new PoiEntry(row.PoiId, row.DestinationId, row.DestinationSlug, row.Name, names.En, names.Aliases ?? [], row.City, row.ImportanceScore, row.IsPublished, row.Version);
    }

    public static void CopyTo(this PoiEntry entry, PoiDirectoryRow row, DateTimeOffset now)
    {
        row.DestinationId = entry.DestinationId;
        row.DestinationSlug = entry.DestinationSlug;
        row.Name = entry.NameFr;
        row.Names = JsonSerializer.Serialize(new NamesJson(entry.NameFr, entry.NameEn, entry.Aliases), Json);
        row.City = entry.City;
        row.ImportanceScore = (short)Math.Clamp(entry.ImportanceScore, 0, 100);
        row.IsPublished = entry.IsPublished;
        row.Version = entry.Version;
        row.SearchText = entry.SearchText;
        row.UpdatedAt = now;
    }

    private sealed record LinkJson(string Kind, string Url);

    private sealed record ChapterJson(int StartSeconds, string Title);

    private sealed record NamesJson(string Fr, string? En, IReadOnlyList<string>? Aliases);
}
