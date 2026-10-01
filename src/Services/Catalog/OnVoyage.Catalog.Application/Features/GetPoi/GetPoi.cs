using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.Catalog.Application.Features.GetPoi;

public sealed record GetPoiQuery(string Slug);

public static class GetPoiHandler
{
    private static readonly string[] Attributions = ["© OpenStreetMap contributors", "Wikipédia (CC BY-SA)"];

    public static async Task<Result<PoiDetailDto>> Handle(GetPoiQuery query, IPoiReader reader, CancellationToken cancellationToken)
    {
        var poi = await reader.FindBySlugAsync(query.Slug, cancellationToken);
        if (poi is null)
        {
            return Result.Failure<PoiDetailDto>("poi_not_found", "Place not found.");
        }

        var stories = poi.Stories
            .Select(story => new StoryDto(story.Id, story.Language, story.Title, story.Text, story.DurationSeconds, story.AudioUrl, story.AiGenerated))
            .ToArray();

        string[] attributions = [.. Attributions, .. poi.Stories.SelectMany(story => story.Sources ?? []).Distinct(StringComparer.Ordinal)];
        return Result.Success(new PoiDetailDto(
            poi.Id, poi.Slug, poi.Name, poi.Category, poi.Location.Latitude, poi.Location.Longitude,
            poi.Importance, poi.CrowdLevel, poi.HiddenGem, stories, attributions,
            [.. (poi.Links ?? []).Select(link => new LinkDto(link.Kind, link.Language, link.Title, link.Url, link.Channel, link.ThumbnailUrl, link.VideoId))]));
    }
}
