using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Home;
using OnVoyage.App.Core.Interactions;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Core.Surprise;

public enum SurpriseStatus
{
    Found,

    /// <summary>Every eligible place was proposed lately or none is eligible here (fragile, regulated, crowded places are never proposed).</summary>
    NothingLeft,

    /// <summary>The server could not be reached.</summary>
    Unavailable,
}

/// <summary>One place drawn for the traveler, with the sentence that says why (F-12).</summary>
public sealed record SurpriseView(Guid PoiId, string Slug, string Name, string? Category, string Explanation, bool IsExploration, int? DistanceMeters);

public sealed record SurpriseOutcome(SurpriseStatus Status, SurpriseView? Surprise = null);

/// <summary>
/// "Surprenez-moi" (F-12, T-615): asks Discovery for one place (<c>GET /api/discovery/v1/surprise</c>, §6.10: a category the traveler explored little
/// but close to their tastes, good quality, never fragile, regulated or crowded) and turns the answer into a sentence. The position, when known, is
/// only sent as the <c>lat</c> and <c>lng</c> of that call; the distance shown is computed here from the place's coordinates, which are then dropped.
/// </summary>
public sealed class SurpriseService(IDiscoveryClient discovery, ICatalogClient catalog, IAnalyticsSink analytics)
{
    public const int RadiusMeters = 5_000;

    public async Task<SurpriseOutcome> DrawAsync(Position? from, CancellationToken cancellationToken)
    {
        RecommendationItemDto? item;
        try
        {
            item = await discovery.GetSurpriseAsync(from?.Latitude, from?.Longitude, RadiusMeters, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new SurpriseOutcome(SurpriseStatus.Unavailable);
        }

        analytics.Track("surprise_requested", new Dictionary<string, object?> { ["results_count"] = item is null ? 0 : 1 });
        if (item is null)
        {
            return new SurpriseOutcome(SurpriseStatus.NothingLeft);
        }

        string? category = null;
        int? distance = null;
        try
        {
            if (await catalog.GetPoiAsync(item.Slug, cancellationToken) is { } detail)
            {
                category = detail.Category;
                if (from is { } position)
                {
                    distance = (int)Math.Round(GeoMath.DistanceMeters(position.Latitude, position.Longitude, detail.Latitude, detail.Longitude));
                }
            }
        }
        catch (HttpRequestException)
        {
            // The card is still useful without the category and the distance.
        }

        return new SurpriseOutcome(SurpriseStatus.Found, new SurpriseView(item.PoiId, item.Slug, item.Name, category, Explain(item, category, distance), item.IsExploration, distance));
    }

    /// <summary>The explanation of §6.9 as a French sentence; an exploration pick says it leaves the traveler's habits, and the distance closes it.</summary>
    public static string Explain(RecommendationItemDto item, string? category, int? distanceMeters)
    {
        List<string> sentences = [];
        if (item.IsExploration && category is not null)
        {
            sentences.Add($"Vous n'avez pas encore beaucoup exploré {HomeFeedService.Label(category)}.");
        }

        sentences.Add(Why(item.Why));
        if (distanceMeters is { } meters)
        {
            sentences.Add($"À {DistanceText.Format(meters)} de vous.");
        }

        return string.Join(' ', sentences);
    }

    private static string Why(WhyDto why)
    {
        string Param(string name) => why.Params.TryGetValue(name, out var value) ? value : string.Empty;

        return why.Template switch
        {
            "liked_similar" => $"Vous avez aimé {Param("poiName")} : ce lieu lui ressemble.",
            "categories" => $"Vous aimez : {string.Join(" et ", Param("categories").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(HomeFeedService.Label))}.",
            "creator_followed" => $"Recommandé par @{Param("creator")}, que vous suivez.",
            "creator_similar" => $"Adoré par @{Param("creator")}, créateur proche de vos goûts.",
            "hidden_gem" => "Moins fréquenté, tout aussi riche.",
            "cold_start" when Param("destination") is { Length: > 0 } destination => $"Un incontournable de {destination}.",
            _ => "Un lieu à découvrir.",
        };
    }
}
