using OnVoyage.App.Core.Map;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Core.Tests;

public sealed class MapFeaturesTests
{
    private static readonly Guid Saved = Guid.NewGuid();

    private static PoiSummaryDto Poi(string name, string category, int crowd, int? audio, double lat = 43.3, double lng = 5.37, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name.ToLowerInvariant(), name, category, lat, lng, 0.7, 0.8, crowd, false, null, audio, new Dictionary<string, double>());

    private static readonly PoiSummaryDto[] Pois =
    [
        Poi("Fort", "monument", 4, 90, 43.295, 5.360),
        Poi("Calanque", "nature", 1, null, 43.21, 5.45),
        Poi("Musée", "museum", 2, 120, 43.30, 5.38),
        Poi("Panier", "neighborhood", 3, 60, 43.298, 5.369, Saved),
    ];

    private static IReadOnlySet<Guid> SavedSet => new HashSet<Guid> { Saved };

    [Fact]
    public void No_filter_keeps_every_place()
    {
        MapFeatures.Build(Pois, new MapFilters(), SavedSet).Features.Count.ShouldBe(4);
    }

    [Fact]
    public void Categories_less_crowded_audio_and_saved_each_narrow_the_list()
    {
        Names(new MapFilters { Categories = new HashSet<string> { "museum", "nature" } }).ShouldBe(["Calanque", "Musée"]);
        Names(new MapFilters { LessCrowded = true }).ShouldBe(["Calanque", "Musée"]);
        Names(new MapFilters { WithAudio = true }).ShouldBe(["Fort", "Musée", "Panier"]);
        Names(new MapFilters { SavedOnly = true }).ShouldBe(["Panier"]);
    }

    [Fact]
    public void Filters_combine_with_and()
    {
        Names(new MapFilters { LessCrowded = true, WithAudio = true }).ShouldBe(["Musée"]);
    }

    [Fact]
    public void Coordinates_are_longitude_then_latitude_as_geojson_requires()
    {
        var feature = MapFeatures.Build([Pois[0]], new MapFilters(), SavedSet).Features.Single();
        feature.Geometry.Coordinates.ShouldBe([5.360, 43.295]);
        feature.Properties.Saved.ShouldBeFalse();
        feature.Properties.Audio.ShouldBeTrue();
    }

    [Fact]
    public void Bounds_cover_all_the_places_and_are_null_when_there_are_none()
    {
        var all = MapFeatures.BoundsOf(MapFeatures.Build(Pois, new MapFilters(), SavedSet))!;
        all.West.ShouldBe(5.360);
        all.East.ShouldBe(5.45);
        all.South.ShouldBe(43.21);
        all.North.ShouldBe(43.30);
        MapFeatures.BoundsOf(MapFeatures.Build(Pois, new MapFilters { Categories = new HashSet<string> { "none" } }, SavedSet)).ShouldBeNull();
    }

    [Fact]
    public void Serialises_as_a_geojson_feature_collection()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(MapFeatures.Build([Pois[1]], new MapFilters(), SavedSet));
        json.ShouldContain("\"type\":\"FeatureCollection\"");
        json.ShouldContain("\"coordinates\":[5.45,43.21]");
        json.ShouldContain("\"crowd\":1");
    }

    [Fact]
    public void The_attribution_names_openstreetmap_and_protomaps()
    {
        MapSettings.Attribution.ShouldContain("OpenStreetMap contributors");
        MapSettings.Attribution.ShouldContain("Protomaps");
    }

    private static string[] Names(MapFilters filters) => [.. MapFeatures.Build(Pois, filters, SavedSet).Features.Select(f => f.Properties.Name).Order(StringComparer.Ordinal)];
}
