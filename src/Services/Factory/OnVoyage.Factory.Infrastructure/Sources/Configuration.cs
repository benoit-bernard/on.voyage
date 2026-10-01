using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Factory.Domain.Geo;

namespace OnVoyage.Factory.Infrastructure.Sources;

/// <summary>Destinations are configuration (<c>Factory:Destinations</c>): activating a new one is a settings change, not a release (D-13).</summary>
internal sealed class ConfiguredDestinationCatalog(IConfiguration configuration) : IDestinationCatalog
{
    public Task<DestinationConfig?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        var section = configuration.GetSection("Factory:Destinations").GetChildren().FirstOrDefault(child => string.Equals(child["Slug"], slug, StringComparison.OrdinalIgnoreCase));
        if (section is null)
        {
            return Task.FromResult<DestinationConfig?>(null);
        }

        var bbox = section.GetSection("BoundingBox").Get<double[]>() ?? throw new InvalidOperationException($"Destination '{slug}' needs a BoundingBox [minLon, minLat, maxLon, maxLat].");
        if (bbox.Length != 4)
        {
            throw new InvalidOperationException($"Destination '{slug}' BoundingBox must have four numbers.");
        }

        return Task.FromResult<DestinationConfig?>(new DestinationConfig(
            section["Slug"]!,
            section["Name"] ?? section["Slug"]!,
            new GeoPoint(section.GetValue<double>("CenterLatitude"), section.GetValue<double>("CenterLongitude")),
            bbox[0], bbox[1], bbox[2], bbox[3],
            section["OsmExtractUrl"] ?? "https://download.geofabrik.de/europe/france/provence-alpes-cote-d-azur-latest.osm.pbf",
            section["OsmExtractFile"]));
    }
}

/// <summary>The shipped <c>data-pipeline/taxonomy/mappings.json</c>, or the file named by <c>Factory:ClassificationRulesPath</c> for an editor's version.</summary>
internal sealed class JsonClassificationRuleProvider : IClassificationRuleProvider
{
    public JsonClassificationRuleProvider(IConfiguration configuration)
    {
        var path = configuration["Factory:ClassificationRulesPath"];
        using var stream = path is null
            ? typeof(JsonClassificationRuleProvider).Assembly.GetManifestResourceStream("mappings.json") ?? throw new InvalidOperationException("mappings.json is missing.")
            : File.OpenRead(path);
        Current = Parse(stream);
    }

    public ClassificationRuleSet Current { get; }

    internal static ClassificationRuleSet Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var rules = new Dictionary<string, IReadOnlyList<RuleWeight>>(StringComparer.Ordinal);
        foreach (var rule in document.RootElement.GetProperty("rules").EnumerateObject())
        {
            rules[rule.Name] = [.. rule.Value.EnumerateArray().Select(entry => new RuleWeight(entry.GetProperty("code").GetString()!, entry.GetProperty("weight").GetDouble()))];
        }

        return new ClassificationRuleSet(document.RootElement.GetProperty("version").GetInt32(), rules);
    }
}

/// <summary>Until a language model is configured (T-301 onward) the fallback is absent: unclassified places go to review rather than guessing.</summary>
internal sealed class NoModelClassifier : IPlaceModelClassifier
{
    public Task<ModelClassification?> ClassifyAsync(PlaceDescription place, CancellationToken cancellationToken) => Task.FromResult<ModelClassification?>(null);
}
