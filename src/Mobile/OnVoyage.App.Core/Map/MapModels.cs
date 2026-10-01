using System.Text.Json.Serialization;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Core.Map;

/// <summary>Where the vector tiles and fonts come from. A missing <see cref="TilesUrl"/> means no map is configured yet; the page says so instead of failing.</summary>
public sealed record MapSettings
{
    /// <summary>A <c>.pmtiles</c> file served with HTTP Range requests (§14.4), or the app's own offline URL.</summary>
    public string? TilesUrl { get; init; }

    /// <summary>Glyph URL template with <c>{fontstack}</c> and <c>{range}</c>; the fonts ship with the app so no third party sees the traveler's browsing.</summary>
    public string GlyphsUrl { get; init; } = "_content/OnVoyage.UI.Components/fonts/{fontstack}/{range}.pbf";

    /// <summary>Shown on the map, always. OpenStreetMap's licence and the Protomaps basemap's terms both require it.</summary>
    public const string Attribution = "© OpenStreetMap contributors · Protomaps";
}

/// <summary>The four filters of F-04. Empty <see cref="Categories"/> means every category.</summary>
public sealed record MapFilters
{
    public IReadOnlySet<string> Categories { get; init; } = new HashSet<string>();

    /// <summary>"Moins fréquentés": crowd level 2 or lower.</summary>
    public bool LessCrowded { get; init; }

    public bool WithAudio { get; init; }

    public bool SavedOnly { get; init; }

    public const int LessCrowdedMaxLevel = 2;

    public bool Matches(PoiSummaryDto poi, IReadOnlySet<Guid> saved) =>
        (Categories.Count == 0 || Categories.Contains(poi.Category))
        && (!LessCrowded || poi.CrowdLevel <= LessCrowdedMaxLevel)
        && (!WithAudio || poi.StoryId is not null || poi.AudioSeconds is > 0)
        && (!SavedOnly || saved.Contains(poi.Id));
}

public sealed record MapBounds(double West, double South, double East, double North);

public sealed record MapFeatureProperties(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("crowd")] int Crowd,
    [property: JsonPropertyName("audio")] bool Audio,
    [property: JsonPropertyName("saved")] bool Saved);

public sealed record MapGeometry([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("coordinates")] double[] Coordinates);

public sealed record MapFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("geometry")] MapGeometry Geometry,
    [property: JsonPropertyName("properties")] MapFeatureProperties Properties);

public sealed record MapGeoJson(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("features")] IReadOnlyList<MapFeature> Features);

public static class MapFeatures
{
    /// <summary>GeoJSON for the places that pass the filters. GeoJSON order is longitude then latitude.</summary>
    public static MapGeoJson Build(IEnumerable<PoiSummaryDto> pois, MapFilters filters, IReadOnlySet<Guid> saved) =>
        new("FeatureCollection", [.. pois
            .Where(poi => filters.Matches(poi, saved))
            .Select(poi => new MapFeature(
                "Feature",
                new MapGeometry("Point", [poi.Longitude, poi.Latitude]),
                new MapFeatureProperties(poi.Id.ToString(), poi.Name, poi.Category, poi.CrowdLevel, poi.StoryId is not null || poi.AudioSeconds is > 0, saved.Contains(poi.Id))))]);

    /// <summary>The box around the places, for <c>fitBounds</c>; null when there are none.</summary>
    public static MapBounds? BoundsOf(MapGeoJson collection)
    {
        if (collection.Features.Count == 0)
        {
            return null;
        }

        var lngs = collection.Features.Select(f => f.Geometry.Coordinates[0]).ToArray();
        var lats = collection.Features.Select(f => f.Geometry.Coordinates[1]).ToArray();
        return new MapBounds(lngs.Min(), lats.Min(), lngs.Max(), lats.Max());
    }
}
