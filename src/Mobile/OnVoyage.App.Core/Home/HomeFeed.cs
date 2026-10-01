using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Recommendation.Engine;
using OnVoyage.Taxonomy;

namespace OnVoyage.App.Core.Home;

public sealed record PlaceCard(PoiSummaryDto Poi, int? CompatibilityPercent, string Badge, string Why, bool Saved);

public sealed record HomeFeed(string DestinationName, string ForYouTitle, bool IsControl, IReadOnlyList<PlaceCard> ForYou, IReadOnlyList<PlaceCard> LessCrowded, IReadOnlyList<PlaceCard> Saved);

/// <summary>Builds the home screen (F-03) on the device. Ranking runs locally so the taste profile never leaves the phone.</summary>
public sealed class HomeFeedService(ICatalogClient catalog, IProfileStore profiles, ISessionProvider sessions)
{
    private const int ForYouCount = 5;
    private const int LessCrowdedCount = 3;

    public async Task<HomeFeed> BuildAsync(double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        var profile = await profiles.LoadAsync(cancellationToken);

        // The traveler id is the account id (F-01), so the control cohort follows the account across devices.
        var session = await sessions.EnsureSessionAsync(cancellationToken);
        if (profile.TravelerId != session.TravelerId)
        {
            profile = profile with { TravelerId = session.TravelerId };
            await profiles.SaveAsync(profile, cancellationToken);
        }

        var destination = await catalog.GetDestinationAsync(profile.Destination, cancellationToken);
        var pois = await catalog.GetPoisAsync(profile.Destination, latitude, longitude, cancellationToken);
        var byId = pois.ToDictionary(poi => poi.Id.ToString("N"));
        var candidates = pois.Select(ToCandidate).ToArray();
        var control = ControlCohort.Contains(profile.TravelerId);

        IReadOnlyList<PlaceCard> forYou;
        if (control)
        {
            forYou = [.. Recommender.RankControl(candidates, TravelMode.Walk)
                .Take(ForYouCount)
                .Select(candidate => new PlaceCard(byId[candidate.Id], null, "Populaire", "Un incontournable de la ville.", profile.Saved.Contains(byId[candidate.Id].Id)))];
        }
        else
        {
            forYou = [.. Recommender.Rank(ToTaste(profile), candidates, TravelMode.Walk)
                .Take(ForYouCount)
                .Select(scored => ToCard(scored, byId[scored.Candidate.Id], profile))];
        }

        var shown = forYou.Select(card => card.Poi.Id).ToHashSet();
        var lessCrowded = pois
            .Where(poi => poi.HiddenGem && !shown.Contains(poi.Id))
            .OrderByDescending(poi => poi.Importance)
            .Take(LessCrowdedCount)
            .Select(poi => new PlaceCard(poi, null, "Pépite", "Moins fréquenté, tout aussi riche.", profile.Saved.Contains(poi.Id)))
            .ToArray();

        var saved = pois
            .Where(poi => profile.Saved.Contains(poi.Id))
            .Select(poi => new PlaceCard(poi, null, poi.HiddenGem ? "Pépite" : "Populaire", string.Empty, true))
            .ToArray();

        return new HomeFeed(destination?.Name ?? "Marseille", control ? "Incontournables" : "Pour vous", control, forYou, lessCrowded, saved);
    }

    internal static Candidate ToCandidate(PoiSummaryDto poi) =>
        new(poi.Id.ToString("N"), poi.Weights, poi.Importance, poi.Quality, poi.DistanceMeters, poi.CrowdLevel, poi.HiddenGem);

    internal static TasteProfile ToTaste(LocalProfile profile) => new(profile.Affinities, profile.Depth);

    private static PlaceCard ToCard(ScoredCandidate scored, PoiSummaryDto poi, LocalProfile profile)
    {
        var badge = scored.CompatibilityPercent is { } percent ? $"{percent} %" : poi.HiddenGem ? "Pépite" : "Populaire";
        return new PlaceCard(poi, scored.CompatibilityPercent, badge, Explain(scored.Reason), profile.Saved.Contains(poi.Id));
    }

    internal static string Explain(Reason reason) => reason.Code switch
    {
        ReasonCode.Categories => $"Vous aimez : {string.Join(" et ", reason.Categories.Select(Label))}.",
        ReasonCode.HiddenGem => "Moins fréquenté, tout aussi riche.",
        _ => "Un incontournable de la ville.",
    };

    public static string Label(string code) => Labels.TryGetValue(code, out var label) ? label : code.Replace('_', ' ').Replace('.', ' ');

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["history"] = "l'histoire",
        ["architecture"] = "l'architecture",
        ["nature"] = "la nature",
        ["culture"] = "la culture et les arts",
        ["religion"] = "le patrimoine religieux",
        ["villages"] = "les villages",
        ["gastronomy"] = "la gastronomie",
        ["curiosities"] = "les curiosités",
        ["outdoors"] = "le plein air",
        ["leisure"] = "les plages et la détente",
    };

    public static IReadOnlyList<(string Code, string Label)> OnboardingCategories { get; } =
        [.. Interests.LevelOne.Select(code => (code, Label(code)))];
}
