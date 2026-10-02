using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Content;
using OnVoyage.Factory.Application.Features.Places;
using OnVoyage.Factory.Application.Features.Videos;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;
using OnVoyage.Factory.Domain.Geo;
using OnVoyage.Taxonomy;

namespace OnVoyage.Factory.Application.Features.Snapshot;

// A versioned, committed content snapshot of a destination (data-pipeline/<slug>/): places with their scores and ethics fields, and a
// first story each, flagged as an AI-written draft that still needs a person's review (ADR-0017).

public sealed record SnapshotDestination(int SchemaVersion, string Slug, string Name, double Latitude, double Longitude, string SnapshotVersion, string Review, string Note);

public sealed record SnapshotSource(string Title, string? Publisher, string Url, string License);

public sealed record SnapshotStory(string Title, string Hook, string Text, string RemoteIntro, string AnnounceFront, string AnnounceLeft, string AnnounceRight, string? CareNote);

public sealed record SnapshotCrowd(int Offpeak, int Shoulder, int Peak);

public sealed record SnapshotPoi(
    string Slug,
    string Name,
    string? NameEn,
    double Latitude,
    double Longitude,
    IReadOnlyDictionary<string, double> Interests,
    int Importance,
    int PopularityPercentile,
    bool HiddenGem,
    SnapshotCrowd Crowd,
    bool Fragile,
    bool AccessRegulated,
    string? WikipediaFr,
    string? WikipediaEn,
    SnapshotStory Story,
    IReadOnlyList<SnapshotSource> Sources);

public sealed record SnapshotBundle(SnapshotDestination Destination, IReadOnlyList<SnapshotPoi> Pois);

/// <summary>Reads the snapshot of a destination; <c>null</c> when none is committed.</summary>
public interface ISnapshotSource
{
    Task<SnapshotBundle?> LoadAsync(string destinationSlug, CancellationToken cancellationToken);
}

/// <summary>What the store needs to create or update a snapshot place. <see cref="ContentSha256"/> detects an unchanged place.</summary>
public sealed record SnapshotPlaceInput(
    Guid Id,
    string DestinationSlug,
    string Slug,
    string Name,
    string? NameEn,
    GeoPoint Location,
    string ContentSha256,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, double> Interests,
    int Importance,
    int Percentile,
    bool HiddenGem,
    SnapshotCrowd Crowd,
    bool Fragile,
    bool AccessRegulated);

public enum SnapshotPlaceChange
{
    Unchanged,
    Created,
    Updated,
}

public sealed record ImportSnapshotCommand(string DestinationSlug);

public sealed record SnapshotImportSummary(
    string DestinationSlug, string SnapshotVersion, int Places, int PlacesPublished, int StoriesPublished, int StoriesWithAudio, int StoriesTextOnly, int Unchanged, int Failed);

/// <summary>Deterministic identifiers: re-importing the same snapshot, on any machine, addresses the same rows.</summary>
public static class SnapshotIds
{
    public static Guid Place(string destination, string slug) => From($"place:{destination}:{slug}");

    public static Guid Story(Guid placeId, string lang, string kind, int version) => From($"story:{placeId:N}:{lang}:{kind}:{version}");

    public static Guid Document(Guid placeId, string url) => From($"document:{placeId:N}:{url}");

    public static Guid Fact(Guid placeId) => From($"fact:{placeId:N}:snapshot");

    /// <summary>Stable positive identifier used in the unique (osm_type, osm_id) index for places that do not come from OpenStreetMap.</summary>
    public static long SyntheticOsmId(string destination, string slug) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"osm:{destination}:{slug}")), 0) & long.MaxValue;

    private static Guid From(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        Span<byte> bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (custom), RFC 9562
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public static class ImportSnapshotHandler
{
    public const string Kind = "standard";
    public const string Lang = "fr";
    public const string PromptPrefix = "snapshot-1:";
    public const string PlaceHashTag = "onvoyage:snapshot_sha256";

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    /// <summary>
    /// Loads the committed snapshot into the pipeline: places (scores and ethics as committed), then each story through the same states,
    /// audio and publication handlers as a generated one, so Catalog, Discovery and Creators receive the usual events. Idempotent: an
    /// unchanged place or story is skipped, an interrupted one resumes, a changed one becomes a new version.
    /// </summary>
    public static async Task<Result<SnapshotImportSummary>> Handle(
        ImportSnapshotCommand command,
        ISnapshotSource source,
        IDestinationCatalog destinations,
        ITextToSpeechProvider speech,
        IAudioProcessor processor,
        IMediaStorage storage,
        IContentSettingsProvider settings,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<ImportSnapshotCommand> logger,
        CancellationToken cancellationToken)
    {
        if (await destinations.FindAsync(command.DestinationSlug, cancellationToken) is null)
        {
            return Result.Failure<SnapshotImportSummary>("destination_not_found", "Unknown destination.");
        }

        var bundle = await source.LoadAsync(command.DestinationSlug, cancellationToken);
        if (bundle is null)
        {
            return Result.Failure<SnapshotImportSummary>("snapshot_not_found", $"No snapshot is committed for '{command.DestinationSlug}'.");
        }

        var invalid = FindProblem(bundle);
        if (invalid is not null)
        {
            return Result.Failure<SnapshotImportSummary>("snapshot_invalid", invalid);
        }

        int placesPublished = 0, storiesPublished = 0, withAudio = 0, textOnly = 0, unchanged = 0, failed = 0;
        foreach (var poi in bundle.Pois)
        {
            try
            {
                // One scope (one unit of work, one outbox, one change tracker) per place: a failure leaves the others untouched.
                await using var scope = scopes.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var outcome = await ImportPoiAsync(
                    bundle.Destination, poi, services.GetRequiredService<IPlaceStore>(), destinations, services.GetRequiredService<IVideoStore>(), services.GetRequiredService<IContentStore>(),
                    speech, processor, storage, settings, clock, logger, cancellationToken);
                placesPublished += outcome.PlacePublished ? 1 : 0;
                storiesPublished += outcome.StoryPublished ? 1 : 0;
                withAudio += outcome.StoryPublished && outcome.WithAudio ? 1 : 0;
                textOnly += outcome.StoryPublished && !outcome.WithAudio ? 1 : 0;
                unchanged += outcome is { PlacePublished: false, StoryPublished: false } ? 1 : 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(exception, "Snapshot place {Slug} could not be imported; the next run resumes it.", poi.Slug);
            }
        }

        logger.LogInformation(
            "Snapshot {Destination} v{Version}: {Places} places, {PlacesPublished} (re)published, {Stories} stories published ({Audio} with audio, {Text} text only), {Unchanged} unchanged, {Failed} failed.",
            command.DestinationSlug, bundle.Destination.SnapshotVersion, bundle.Pois.Count, placesPublished, storiesPublished, withAudio, textOnly, unchanged, failed);
        return Result.Success(new SnapshotImportSummary(command.DestinationSlug, bundle.Destination.SnapshotVersion, bundle.Pois.Count, placesPublished, storiesPublished, withAudio, textOnly, unchanged, failed));
    }

    private sealed record PoiOutcome(bool PlacePublished, bool StoryPublished, bool WithAudio);

    private static async Task<PoiOutcome> ImportPoiAsync(
        SnapshotDestination destination,
        SnapshotPoi poi,
        IPlaceStore places,
        IDestinationCatalog destinations,
        IVideoStore videos,
        IContentStore content,
        ITextToSpeechProvider speech,
        IAudioProcessor processor,
        IMediaStorage storage,
        IContentSettingsProvider settings,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var placeId = SnapshotIds.Place(destination.Slug, poi.Slug);
        var placeHash = SnapshotIds.Sha256(JsonSerializer.Serialize(poi with { Story = null!, Sources = [] }, Canonical));
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { [PlaceHashTag] = placeHash };
        if (poi.WikipediaFr is { Length: > 0 } fr)
        {
            tags["wikipedia"] = $"fr:{fr}";
        }

        if (poi.WikipediaEn is { Length: > 0 } en)
        {
            tags["wikipedia:en"] = en;
        }

        var input = new SnapshotPlaceInput(
            placeId, destination.Slug, poi.Slug, poi.Name, poi.NameEn, new GeoPoint(poi.Latitude, poi.Longitude), placeHash, tags,
            poi.Interests, poi.Importance, poi.PopularityPercentile, poi.HiddenGem, poi.Crowd, poi.Fragile, poi.AccessRegulated);
        var change = await places.UpsertSnapshotPlaceAsync(input, cancellationToken);
        var place = await places.FindAsync(placeId, cancellationToken) ?? throw new InvalidOperationException("The snapshot place disappeared after being saved.");

        // An editor's decision on the place (rejected, unpublished, merged into another) is never overturned by a re-import.
        if (place.Status is PlaceStatus.Rejected or PlaceStatus.Unpublished or PlaceStatus.Merged)
        {
            logger.LogInformation("Snapshot place {Slug} is {Status}; left as it is.", poi.Slug, place.Status);
            return new PoiOutcome(false, false, false);
        }

        var placePublished = false;
        if (change != SnapshotPlaceChange.Unchanged || place.Status != PlaceStatus.Published)
        {
            var published = await PublishPlaceHandler.Handle(new PublishPlaceCommand(placeId), places, destinations, videos, clock, cancellationToken);
            if (!published.IsSuccess)
            {
                throw new InvalidOperationException($"Place {poi.Slug} was not published: {published.Error!.Message}");
            }

            placePublished = true;
        }

        var storyHash = SnapshotIds.Sha256(JsonSerializer.Serialize(new { poi.Story, poi.Sources }, Canonical));
        var promptVersion = PromptPrefix + storyHash[..16];
        var stories = (await content.ListStoriesAsync(placeId, cancellationToken))
            .Where(story => story.Lang == Lang && story.Kind == StoryKind.Standard)
            .OrderByDescending(story => story.Version)
            .ToList();
        var current = stories.FirstOrDefault(story => story.PromptVersion == promptVersion);

        StoryRecord story;
        if (current is not null && current.Status == ContentStatus.Published)
        {
            var parts = await content.ListAudioPartsAsync(current.Id, cancellationToken);
            var hasAudio = parts.Any(part => part.Part == "main");
            if (hasAudio || !speech.IsAvailable)
            {
                return new PoiOutcome(placePublished, false, hasAudio);
            }

            // Published as text only earlier and a voice exists now: a new version carries the audio (a consumer ignores a repeated version).
            story = await NewVersionAsync(destination, poi, placeId, promptVersion, content, settings, clock, cancellationToken);
        }
        else if (current is { Status: ContentStatus.Approved or ContentStatus.AudioReady })
        {
            story = current; // an interrupted run: resume
        }
        else if (current is not null)
        {
            logger.LogInformation("Snapshot story of {Slug} is {Status}; left as it is.", poi.Slug, current.Status);
            return new PoiOutcome(placePublished, false, false);
        }
        else
        {
            story = await NewVersionAsync(destination, poi, placeId, promptVersion, content, settings, clock, cancellationToken);
        }

        if (speech.IsAvailable && story.Status == ContentStatus.Approved)
        {
            var audio = await GenerateAudioHandler.Handle(new GenerateAudioCommand(story.Id), null, content, places, speech, processor, storage, settings, clock, cancellationToken);
            if (!audio.IsSuccess)
            {
                throw new InvalidOperationException($"Audio of {poi.Slug} failed: {audio.Error!.Message}");
            }
        }

        var publication = await StoryPublicationHandler.Handle(new PublishStoryCommand(story.Id, AllowTextOnly: !speech.IsAvailable), content, places, clock, cancellationToken);
        if (!publication.IsSuccess)
        {
            throw new InvalidOperationException($"Story of {poi.Slug} was not published: {publication.Error!.Message}");
        }

        var voiced = (await content.ListAudioPartsAsync(story.Id, cancellationToken)).Any(part => part.Part == "main");
        return new PoiOutcome(placePublished, true, voiced);
    }

    private static async Task<StoryRecord> NewVersionAsync(
        SnapshotDestination destination, SnapshotPoi poi, Guid placeId, string promptVersion, IContentStore content, IContentSettingsProvider settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var documents = new List<SourceDocument>();
        foreach (var source in poi.Sources)
        {
            var url = source.Url.Trim();
            var text = string.Empty;
            documents.Add(new SourceDocument(
                SnapshotIds.Document(placeId, url), placeId, "wikipedia", url, source.Title, source.Publisher, source.License, Lang, destination.SnapshotVersion, now, text,
                SnapshotIds.Sha256(url), settings.Current.SourceQuality.GetValueOrDefault("wikipedia", 0.7)));
        }

        foreach (var document in documents)
        {
            await content.SaveDocumentAsync(document, cancellationToken);
        }

        // One placeholder fact ties the story to its sources so the publication event carries them; the snapshot is general knowledge
        // written by a model, not statements quoted from these pages (hence the zero-length quote and the modest confidence).
        var factId = SnapshotIds.Fact(placeId);
        if (await content.FindFactAsync(factId, cancellationToken) is null && documents.Count > 0)
        {
            await content.AddFactsAsync(
                [new FactRecord(factId, placeId, documents[0].Id, $"Texte d'amorçage de « {poi.Name} », rédigé par IA à partir de connaissances générales, à relire.", FactType.Event, string.Empty, 0.6, FactStatus.Validated, "snapshot")],
                cancellationToken);
        }

        var version = await content.NextVersionAsync(placeId, Lang, StoryKind.Standard, cancellationToken);
        var words = poi.Story.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var story = new StoryRecord(
            SnapshotIds.Story(placeId, Lang, Kind, version), placeId, Lang, StoryKind.Standard, version, ContentStatus.Approved,
            poi.Story.Title, poi.Story.Hook, poi.Story.Text, poi.Story.RemoteIntro, poi.Story.AnnounceFront, poi.Story.AnnounceLeft, poi.Story.AnnounceRight, poi.Story.CareNote,
            documents.Count > 0 ? [factId] : [], (int)Math.Round(words / 150d * 60d), promptVersion, "snapshot", 0.7,
            new CheckReport([], [], null, 0, ["Brouillon rédigé par IA à partir de connaissances générales : relecture humaine requise (docs/adr/0017)."]),
            settings.Current.VoiceFor(Lang), 0.5, null, now, now, null);
        await content.SaveStoryAsync(story, cancellationToken);
        return story;
    }

    /// <summary>Static checks of a committed file: they catch a typo in a code or a coordinate before anything is written.</summary>
    public static string? FindProblem(SnapshotBundle bundle)
    {
        var known = Interests.All.ToHashSet(StringComparer.Ordinal);
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var poi in bundle.Pois)
        {
            if (string.IsNullOrWhiteSpace(poi.Slug) || !slugs.Add(poi.Slug))
            {
                return $"Missing or duplicate slug '{poi.Slug}'.";
            }

            if (poi.Latitude is < -90 or > 90 || poi.Longitude is < -180 or > 180)
            {
                return $"{poi.Slug}: coordinates out of range.";
            }

            if (poi.Interests.Count == 0 || poi.Interests.Any(pair => !known.Contains(pair.Key) || pair.Value is <= 0 or > 1))
            {
                return $"{poi.Slug}: interests must be taxonomy codes with a weight in (0, 1].";
            }

            if (poi.Importance is < 0 or > 100 || poi.PopularityPercentile is < 0 or > 100)
            {
                return $"{poi.Slug}: importance and percentile are between 0 and 100.";
            }

            if (new[] { poi.Crowd.Offpeak, poi.Crowd.Shoulder, poi.Crowd.Peak }.Any(level => level is < 1 or > 5))
            {
                return $"{poi.Slug}: crowd levels are between 1 and 5.";
            }

            if (string.IsNullOrWhiteSpace(poi.Story.Text) || string.IsNullOrWhiteSpace(poi.Story.Title))
            {
                return $"{poi.Slug}: the story needs a title and a text.";
            }

            if (poi.Sources.Count == 0 || poi.Sources.Any(source => !Uri.TryCreate(source.Url, UriKind.Absolute, out _)))
            {
                return $"{poi.Slug}: at least one source with an absolute URL is required.";
            }
        }

        return null;
    }
}
