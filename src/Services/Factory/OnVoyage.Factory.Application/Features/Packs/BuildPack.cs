using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;
using OnVoyage.Packs;
using OnVoyage.Taxonomy;

namespace OnVoyage.Factory.Application.Features.Packs;

/// <summary>Builds the offline pack of a destination and language (F-15, §14.6) from what is published now, and publishes it as a new version.</summary>
public sealed record BuildPackCommand(string DestinationSlug, string Lang = "fr");

/// <summary>Writes the archive and its <c>latest.json</c> where travelers download them from (media storage in MVP-0, a private bucket with signed URLs once Billing exists).</summary>
public interface IPackPublisher
{
    /// <summary>The pack currently published for the destination and language, or null.</summary>
    Task<PackInfo?> LatestAsync(string destinationSlug, string lang, CancellationToken cancellationToken);

    Task<PackInfo> PublishAsync(PackContent content, CancellationToken cancellationToken);
}

/// <summary>Cuts the map extract of the destination (PMTiles, zoom 0–15). Null when no map source is configured: the pack then has no <c>map.pmtiles</c>.</summary>
public interface IMapExtractor
{
    Task<string?> ExtractAsync(DestinationConfig destination, CancellationToken cancellationToken);
}

public sealed class NoMapExtractor : IMapExtractor
{
    public Task<string?> ExtractAsync(DestinationConfig destination, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

public static class BuildPackHandler
{
    public static readonly string[] Languages = ["fr", "en"];

    /// <summary>The taxonomy version the interest codes of the pack belong to (the app checks it against its own).</summary>
    public static string TaxonomyVersion => Interests.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<Result<PackInfo>> Handle(
        BuildPackCommand command,
        IDestinationCatalog destinations,
        IPlaceStore places,
        IContentStore content,
        IPackPublisher publisher,
        IMapExtractor maps,
        CancellationToken cancellationToken)
    {
        if (!Languages.Contains(command.Lang))
        {
            return Result.Failure<PackInfo>("validation", "Language must be fr or en.");
        }

        var destination = await destinations.FindAsync(command.DestinationSlug, cancellationToken);
        if (destination is null)
        {
            return Result.Failure<PackInfo>("destination_not_found", "Unknown destination.");
        }

        List<PackPlace> packPlaces = [];
        foreach (var place in await places.ListAsync(destination.Slug, PlaceStatus.Published, 5_000, cancellationToken))
        {
            var interests = (await places.GetInterestsAsync(place.Id, cancellationToken)).ToDictionary(item => item.Code, item => item.Weight, StringComparer.Ordinal);
            if (interests.Count == 0)
            {
                continue; // never classified: the engine could not rank it, the catalog does not have it either
            }

            var detail = await places.GetScoreDetailAsync(place.Id, cancellationToken);
            var stories = await StoriesAsync(place, command.Lang, content, cancellationToken);
            var category = Interests.LevelOneOf(interests.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key);
            packPlaces.Add(new PackPlace(
                place.Id,
                place.Slug,
                command.Lang == "en" ? place.Enrichment?.LabelEn ?? place.NameEn ?? place.Name : place.Enrichment?.LabelFr ?? place.Name,
                category,
                place.Location.Latitude,
                place.Location.Longitude,
                detail.Importance,
                detail.Peak,
                detail.Fragile,
                detail.AccessRegulated,
                detail.HiddenGem,
                Quality: 0.8d,
                CarAccessible: false,
                VisibleFromRoad: false,
                command.Lang == "en" ? place.Enrichment?.DescriptionEn : place.Enrichment?.DescriptionFr,
                interests,
                stories));
        }

        if (packPlaces.Count == 0)
        {
            return Result.Failure<PackInfo>("pack_empty", "No published place to put in the pack.");
        }

        var latest = await publisher.LatestAsync(destination.Slug, command.Lang, cancellationToken);
        var map = await maps.ExtractAsync(destination, cancellationToken);
        var pack = new PackContent(destination.Slug, command.Lang, (latest?.Version ?? 0) + 1, TaxonomyVersion, MinAppVersion: null, packPlaces, map);
        return Result.Success(await publisher.PublishAsync(pack, cancellationToken));
    }

    /// <summary>The published stories of the language, the latest version of each kind. A story without any voiced part stays in the pack as text.</summary>
    private static async Task<IReadOnlyList<PackStory>> StoriesAsync(PlaceRecord place, string lang, IContentStore content, CancellationToken cancellationToken)
    {
        List<PackStory> result = [];
        var published = (await content.ListStoriesAsync(place.Id, cancellationToken))
            .Where(story => story.Status == ContentStatus.Published && story.Lang == lang && story.Kind is StoryKind.Standard or StoryKind.Anecdote)
            .GroupBy(story => story.Kind)
            .Select(group => group.OrderByDescending(story => story.Version).First());
        foreach (var story in published)
        {
            var parts = await content.ListAudioPartsAsync(story.Id, cancellationToken);
            var facts = (await content.ListFactsAsync(place.Id, cancellationToken)).Where(fact => story.FactsUsed.Contains(fact.Id)).ToList();
            var sources = (await content.ListDocumentsAsync(place.Id, cancellationToken))
                .Where(document => facts.Any(fact => fact.DocumentId == document.Id))
                .Select(document => $"{document.Title} — {document.License}");
            result.Add(new PackStory(
                story.Id,
                story.Version,
                story.Kind == StoryKind.Standard ? "standard" : "anecdote",
                story.Title,
                story.Text,
                story.RemoteIntro,
                parts.Where(part => part.Part == "main").Select(part => part.DurationSeconds).FirstOrDefault(story.EstimatedDurationSeconds),
                AiGenerated: true,
                [.. sources],
                [.. parts.Select(part => new PackAudio(part.Part, part.Path, part.DurationSeconds))]));
        }

        return result;
    }
}
