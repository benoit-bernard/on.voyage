using System.Text.Json;
using NetTopologySuite.Geometries;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Domain.Geo;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal static class PlaceMapper
{
    public const int Srid = 4326;
    public static readonly GeometryFactory Geometry = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(Srid);

    public static Point ToPoint(GeoPoint point) => Geometry.CreatePoint(new Coordinate(point.Longitude, point.Latitude));

    public static GeoPoint ToGeoPoint(Point point) => new(point.Y, point.X);

    public static Footprint? ToFootprint(MultiPolygon? area) =>
        area is null || area.IsEmpty
            ? null
            : new Footprint([.. Enumerable.Range(0, area.NumGeometries)
                .Select(index => (Polygon)area.GetGeometryN(index))
                .Select(polygon => (IReadOnlyList<GeoPoint>)[.. polygon.ExteriorRing.Coordinates.Select(c => new GeoPoint(c.Y, c.X))])]);

    public static PlaceRecord ToRecord(PlaceRow row, WikidataEntityRow? wikidata) => new(
        row.Id,
        row.DestinationSlug,
        row.Slug,
        row.Name,
        row.NameEn,
        ToGeoPoint(row.Location),
        ToFootprint(row.Footprint),
        row.Qid,
        row.OsmType,
        row.OsmId,
        ParseTags(row.OsmTags),
        Enum.Parse<PlaceStatus>(row.Status),
        wikidata is null ? null : ToEnrichment(wikidata),
        row.AnnualPageviews,
        row.ImportanceOverride,
        row.EditoriallySaturated,
        row.PublishedVersion,
        row.ImportanceScore,
        row.PopularityPercentile,
        row.HiddenGem,
        row.ClassificationOutcome);

    public static PlaceEnrichment ToEnrichment(WikidataEntityRow row) => new(
        row.Qid, row.LabelFr, row.LabelEn, row.DescriptionFr, row.DescriptionEn, row.InstanceOf, row.HeritageStatuses, row.Inception,
        row.Sitelinks, row.WikipediaFr, row.WikipediaEn, row.Image, row.Website, row.RetrievedAt);

    public static IReadOnlyDictionary<string, string> ParseTags(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                tags[property.Name] = property.Value.GetString()!;
            }
        }

        return tags;
    }

    /// <summary>URL-safe, accent-free identifier derived from the name (e.g. <c>chateau-d-if</c>).</summary>
    public static string Slugify(string name)
    {
        var normalized = OnVoyage.Factory.Domain.Dedup.NameSimilarity.Normalize(name);
        var slug = normalized.Replace(' ', '-');
        return slug.Length == 0 ? "lieu" : slug.Length > 80 ? slug[..80].TrimEnd('-') : slug;
    }
}
