using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Domain.Content;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed class ContentStore(IDbContextOutbox<FactoryDbContext> outbox) : IContentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private FactoryDbContext Db => outbox.DbContext;

    // ---- sources and facts

    public async Task<IReadOnlyList<SourceDocument>> ListDocumentsAsync(Guid placeId, CancellationToken cancellationToken) =>
        [.. (await Db.SourceDocuments.AsNoTracking().Where(row => row.PlaceId == placeId).OrderBy(row => row.Language).ThenBy(row => row.Url).ToListAsync(cancellationToken))
            .Select(row => new SourceDocument(row.Id, row.PlaceId, row.Type, row.Url, row.Title, row.Publisher, row.License, row.Language, row.Revision, row.RetrievedAt, row.Text, row.Sha256, row.Quality))];

    public async Task<bool> SaveDocumentAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        var existing = await Db.SourceDocuments.FirstOrDefaultAsync(row => row.PlaceId == document.PlaceId && row.Url == document.Url, cancellationToken);
        if (existing is not null && existing.Sha256 == document.Sha256)
        {
            return false;
        }

        if (existing is null)
        {
            existing = new SourceDocumentRow { Id = document.Id, PlaceId = document.PlaceId, Url = document.Url };
            Db.SourceDocuments.Add(existing);
        }

        existing.Type = document.Type;
        existing.Title = document.Title;
        existing.Publisher = document.Publisher;
        existing.License = document.License;
        existing.Language = document.Language;
        existing.Revision = document.Revision;
        existing.RetrievedAt = document.RetrievedAt;
        existing.Text = document.Text;
        existing.Sha256 = document.Sha256;
        existing.Quality = document.Quality;
        await Db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<FactRecord>> ListFactsAsync(Guid placeId, CancellationToken cancellationToken) =>
        [.. (await Db.Facts.AsNoTracking().Where(row => row.PlaceId == placeId).OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToListAsync(cancellationToken)).Select(ToFact)];

    public async Task<FactRecord?> FindFactAsync(Guid factId, CancellationToken cancellationToken)
    {
        var row = await Db.Facts.AsNoTracking().FirstOrDefaultAsync(fact => fact.Id == factId, cancellationToken);
        return row is null ? null : ToFact(row);
    }

    public async Task AddFactsAsync(IReadOnlyList<FactRecord> facts, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        Db.Facts.AddRange(facts.Select(fact => new FactRow
        {
            Id = fact.Id,
            PlaceId = fact.PlaceId,
            DocumentId = fact.DocumentId,
            Statement = fact.Statement,
            Type = fact.Type.ToString(),
            Quote = fact.Quote,
            Confidence = fact.Confidence,
            Status = fact.Status.ToString(),
            Reason = fact.Reason,
            CreatedAt = now,
        }));
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetFactStatusAsync(IReadOnlyCollection<Guid> factIds, FactStatus status, string? reason, CancellationToken cancellationToken) =>
        await Db.Facts.Where(row => factIds.Contains(row.Id)).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, status.ToString()).SetProperty(row => row.Reason, reason), cancellationToken);

    private static FactRecord ToFact(FactRow row) =>
        new(row.Id, row.PlaceId, row.DocumentId, row.Statement, Enum.Parse<FactType>(row.Type), row.Quote, row.Confidence, Enum.Parse<FactStatus>(row.Status), row.Reason);

    // ---- stories

    public async Task<IReadOnlyList<StoryRecord>> ListStoriesAsync(Guid placeId, CancellationToken cancellationToken) =>
        [.. (await Db.Stories.AsNoTracking().Where(row => row.PlaceId == placeId).OrderBy(row => row.Lang).ThenBy(row => row.Kind).ThenBy(row => row.Version).ToListAsync(cancellationToken)).Select(ToStory)];

    public async Task<StoryRecord?> FindStoryAsync(Guid storyId, CancellationToken cancellationToken)
    {
        var row = await Db.Stories.AsNoTracking().FirstOrDefaultAsync(story => story.Id == storyId, cancellationToken);
        return row is null ? null : ToStory(row);
    }

    public async Task<int> NextVersionAsync(Guid placeId, string lang, StoryKind kind, CancellationToken cancellationToken)
    {
        var kindName = kind.ToString();
        var latest = await Db.Stories.Where(row => row.PlaceId == placeId && row.Lang == lang && row.Kind == kindName).MaxAsync(row => (int?)row.Version, cancellationToken);
        return (latest ?? 0) + 1;
    }

    public async Task SaveStoryAsync(StoryRecord story, CancellationToken cancellationToken)
    {
        Apply(story);
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveStoryWithEventsAsync(StoryRecord story, IReadOnlyList<object> integrationEvents, StoryRecord? archived, CancellationToken cancellationToken)
    {
        Apply(story);
        if (archived is not null)
        {
            Apply(archived);
        }

        foreach (var integrationEvent in integrationEvents)
        {
            await outbox.PublishAsync(integrationEvent);
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    private void Apply(StoryRecord story)
    {
        var row = Db.Stories.Local.FirstOrDefault(item => item.Id == story.Id) ?? Db.Stories.FirstOrDefault(item => item.Id == story.Id);
        if (row is null)
        {
            row = new StoryRow { Id = story.Id, PlaceId = story.PlaceId, Lang = story.Lang, Kind = story.Kind.ToString(), Version = story.Version, CreatedAt = story.CreatedAt };
            Db.Stories.Add(row);
        }

        row.Status = story.Status.ToString();
        row.Title = story.Title;
        row.Hook = story.Hook;
        row.Text = story.Text;
        row.RemoteIntro = story.RemoteIntro;
        row.AnnounceFront = story.AnnounceFront;
        row.AnnounceLeft = story.AnnounceLeft;
        row.AnnounceRight = story.AnnounceRight;
        row.CareNote = story.CareNote;
        row.FactsUsed = [.. story.FactsUsed];
        row.EstimatedDurationSeconds = story.EstimatedDurationSeconds;
        row.PromptVersion = story.PromptVersion;
        row.Model = story.Model;
        row.QualityScore = story.QualityScore;
        row.CheckReport = JsonSerializer.Serialize(story.Report, Json);
        row.VoiceId = story.VoiceId;
        row.EditorialScore = story.EditorialScore;
        row.RejectedReason = story.RejectedReason;
        row.UpdatedAt = story.UpdatedAt;
        row.PublishedAt = story.PublishedAt;
    }

    private static StoryRecord ToStory(StoryRow row) => new(
        row.Id, row.PlaceId, row.Lang, Enum.Parse<StoryKind>(row.Kind), row.Version, Enum.Parse<ContentStatus>(row.Status), row.Title, row.Hook, row.Text,
        row.RemoteIntro, row.AnnounceFront, row.AnnounceLeft, row.AnnounceRight, row.CareNote, row.FactsUsed, row.EstimatedDurationSeconds, row.PromptVersion,
        row.Model, row.QualityScore, JsonSerializer.Deserialize<CheckReport>(row.CheckReport, Json) ?? CheckReport.Empty, row.VoiceId, row.EditorialScore,
        row.RejectedReason, row.CreatedAt, row.UpdatedAt, row.PublishedAt);

    public async Task<IReadOnlyList<AudioPartRecord>> ListAudioPartsAsync(Guid storyId, CancellationToken cancellationToken) =>
        [.. (await Db.StoryAudioParts.AsNoTracking().Where(row => row.StoryId == storyId).OrderBy(row => row.Part).ToListAsync(cancellationToken))
            .Select(row => new AudioPartRecord(row.Part, row.Path, row.Sha256, row.DurationSeconds, row.Bytes))];

    public async Task SaveAudioPartsAsync(Guid storyId, IReadOnlyList<AudioPartRecord> parts, CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            var row = await Db.StoryAudioParts.FirstOrDefaultAsync(item => item.StoryId == storyId && item.Part == part.Part, cancellationToken);
            if (row is null)
            {
                row = new StoryAudioPartRow { StoryId = storyId, Part = part.Part };
                Db.StoryAudioParts.Add(row);
            }

            row.Path = part.Path;
            row.Sha256 = part.Sha256;
            row.DurationSeconds = part.DurationSeconds;
            row.Bytes = part.Bytes;
        }

        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPronunciationAsync(string destinationSlug, CancellationToken cancellationToken) =>
        await Db.Pronunciations.AsNoTracking().Where(row => row.DestinationSlug == destinationSlug).ToDictionaryAsync(row => row.Term, row => row.Replacement, cancellationToken);

    // ---- reports

    public Task<int> CountReportsByTravelerSinceAsync(Guid travelerId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Db.StoryReports.CountAsync(row => row.TravelerId == travelerId && row.CreatedAt >= since, cancellationToken);

    public async Task<bool> AddReportAsync(StoryReport report, CancellationToken cancellationToken)
    {
        if (await Db.StoryReports.AnyAsync(row => row.StoryId == report.StoryId && row.TravelerId == report.TravelerId, cancellationToken))
        {
            return false;
        }

        Db.StoryReports.Add(new StoryReportRow { Id = report.Id, StoryId = report.StoryId, TravelerId = report.TravelerId, Reason = report.Reason, CreatedAt = report.CreatedAt });
        await Db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<int> CountDistinctReportersAsync(Guid storyId, CancellationToken cancellationToken) =>
        Db.StoryReports.Where(row => row.StoryId == storyId).Select(row => row.TravelerId).Distinct().CountAsync(cancellationToken);

    public async Task<IReadOnlyList<StoryReport>> ListReportsAsync(Guid storyId, CancellationToken cancellationToken) =>
        [.. (await Db.StoryReports.AsNoTracking().Where(row => row.StoryId == storyId).OrderBy(row => row.CreatedAt).ToListAsync(cancellationToken))
            .Select(row => new StoryReport(row.Id, row.StoryId, row.TravelerId, row.Reason, row.CreatedAt))];
}
