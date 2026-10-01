using NetTopologySuite.Geometries;
using OnVoyage.Catalog.Domain;
using OnVoyage.Taxonomy;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

/// <summary>Explicit mapping between persistence rows and Domain entities (copilot-instructions: strict Domain ↔ Infrastructure mapping).</summary>
internal static class PoiMapper
{
    public const int Srid = 4326;
    public static readonly GeometryFactory Geometry = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(Srid);

    public static Point ToPoint(GeoPoint point) => Geometry.CreatePoint(new Coordinate(point.Longitude, point.Latitude));

    public static GeoPoint ToGeoPoint(Point point) => new(point.Y, point.X);

    public static Poi ToDomain(PoiRow row, string lang, string mediaBaseUrl = "/media")
    {
        var name = (row.Texts.FirstOrDefault(text => text.Lang == lang) ?? row.Texts.First()).Name;
        var weights = row.Interests.ToDictionary(interest => interest.TaxonomyCode, interest => (double)interest.Weight);
        var category = weights.Count == 0
            ? "history"
            : Interests.LevelOneOf(weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key);

        var stories = row.Stories
            .Where(story => story.Status == "published" && story.Lang == lang && !story.IsPremium)
            .OrderBy(story => story.Kind, StringComparer.Ordinal).ThenByDescending(story => story.Version)
            .Select(story => new Story(story.Id, story.Lang, story.Title, story.Text ?? string.Empty, story.DurationSeconds, AudioUrl(story, mediaBaseUrl), story.IsAiGenerated, SourceLabels(story)))
            .ToArray();

        return new Poi(
            row.Id,
            row.Slug,
            name,
            category,
            ToGeoPoint(row.Location),
            row.ImportanceScore / 100d,
            row.ContentQualityScore,
            row.Ethics?.CrowdProfile.Peak ?? 1,
            row.HiddenGem,
            weights,
            stories);
    }

    private static string? AudioUrl(StoryRow story, string mediaBaseUrl) =>
        string.IsNullOrEmpty(story.AudioPath) ? null : story.AudioPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? story.AudioPath : $"{mediaBaseUrl.TrimEnd('/')}/{story.AudioPath.TrimStart('/')}";

    private static string[] SourceLabels(StoryRow story) =>
        [.. System.Text.Json.JsonSerializer.Deserialize<List<StorySourceJson>>(story.Sources)?.Select(source => $"{source.Title} — {source.License}") ?? []];

    public static Destination ToDomain(DestinationRow row) => new(row.Slug, row.NameFr, ToGeoPoint(row.Center));
}
