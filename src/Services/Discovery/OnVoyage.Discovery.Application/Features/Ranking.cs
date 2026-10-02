using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Recommendation.Engine;
using OnVoyage.Recommendation.Engine.Learning;
using OnVoyage.Taxonomy;

namespace OnVoyage.Discovery.Application.Features;

/// <summary>The traveler, their vector and the published places that have a story in their language: what every ranking needs.</summary>
internal sealed record RankingContext(
    TravelerInfo Traveler,
    TasteProfile Taste,
    IReadOnlyDictionary<string, double> Vector,
    IReadOnlySet<Guid> Excluded,
    IReadOnlyList<PlaceInfo> Places,
    IReadOnlyList<StoryInfo> Stories,
    IReadOnlyDictionary<Guid, double> Ratings,
    IReadOnlyDictionary<Guid, int> Impressions,
    IReadOnlyDictionary<Guid, IReadOnlyList<CreatorOnPlaceInfo>> CreatorsOnPlaces,
    IReadOnlySet<Guid> Followed,
    RecommendationOptions Options)
{
    public bool Control => Traveler.Cohort == "control";

    public static async Task<RankingContext> LoadAsync(
        Guid travelerId, string? destination, IDiscoveryStore store, ITravelerReader travelers, IPlaceReader places, ICreatorReader creators, TimeProvider clock, CancellationToken cancellationToken)
    {
        var traveler = await travelers.GetAsync(travelerId, cancellationToken)
            ?? new TravelerInfo(travelerId, "fr", "balanced", false, 0, ControlCohort.Contains(travelerId) ? "control" : "personalized");
        var profile = await store.GetProfileAsync(travelerId, cancellationToken);
        var vector = profile?.Learned.Vector ?? new Dictionary<string, double>();
        var excluded = (profile?.Learned.Excluded ?? new HashSet<string>()).Select(Guid.Parse).ToHashSet();
        var stories = await places.StoriesAsync(traveler.Lang, cancellationToken);
        var withStory = stories.Where(s => s.Kind == "standard").Select(s => s.PoiId).ToHashSet();
        var all = (await places.PlacesAsync(destination, cancellationToken)).Where(p => withStory.Contains(p.PoiId)).ToArray();
        var ratings = await travelers.RatingsAsync(travelerId, cancellationToken);
        var impressions = await places.ImpressionsSinceAsync(clock.GetUtcNow().AddDays(-7), cancellationToken);
        var onPlaces = traveler.Cohort == "control" ? new Dictionary<Guid, IReadOnlyList<CreatorOnPlaceInfo>>() : await creators.OnPlacesAsync(destination, cancellationToken);
        var followed = traveler.Cohort == "control" ? new HashSet<Guid>() : await creators.FollowedAsync(travelerId, cancellationToken);
        var ethical = traveler.EthicalMode switch { "off" => EthicalLevel.Off, "strong" => EthicalLevel.Strong, _ => EthicalLevel.Balanced };
        return new RankingContext(traveler, new TasteProfile(vector, traveler.ProfileDepth), vector, excluded, all, stories, ratings, impressions, onPlaces, followed, new RecommendationOptions { Ethical = ethical });
    }

    /// <summary>Hard filters first (§6.6): published, story available, not turned down by the traveler, within the radius.</summary>
    public IReadOnlyList<PlaceInfo> Eligible(double? latitude, double? longitude, int? radiusMeters) => [.. Places
        .Where(p => !Excluded.Contains(p.PoiId))
        .Where(p => radiusMeters is null || latitude is null || longitude is null || Distance(p, latitude.Value, longitude.Value) <= radiusMeters)];

    public static double Distance(PlaceInfo place, double latitude, double longitude) =>
        VisitPlanner_Distance(latitude, longitude, place.Latitude, place.Longitude);

    private static double VisitPlanner_Distance(double lat1, double lon1, double lat2, double lon2) =>
        Recommendation.Engine.Planning.VisitPlanner.Distance(lat1, lon1, lat2, lon2);

    public Candidate ToCandidate(PlaceInfo place, double? latitude, double? longitude, bool remote) => new(
        place.PoiId.ToString("N"),
        place.Weights,
        place.Importance,
        place.Quality,
        remote || latitude is null || longitude is null ? null : Distance(place, latitude.Value, longitude.Value),
        place.CrowdLevel,
        place.HiddenGem,
        Impressions.GetValueOrDefault(place.PoiId),
        EndorsementOf(place));

    /// <summary>The creator signal of §6.15 for one place; none for the control cohort (F-03).</summary>
    public CreatorEndorsement? EndorsementOf(PlaceInfo place) =>
        !Control && CreatorsOnPlaces.TryGetValue(place.PoiId, out var onPlace)
            ? CreatorAffinity.Endorse(Vector, onPlace.Select(c => new CreatorOnPlace(c.Handle, c.Vector, Followed.Contains(c.CreatorId), c.Commercial)))
            : null;

    public IReadOnlyList<ScoredCandidate> Rank(IEnumerable<Candidate> candidates, Recommendation.Engine.TravelMode mode)
    {
        var list = candidates.ToArray();
        if (Control)
        {
            // Control cohort (F-03): importance and distance only, no personal score and no compatibility.
            return [.. Recommender.RankControl(list, mode).Select((c, i) => new ScoredCandidate(c, 1d - (i * 0.0001), null, new Reason(ReasonCode.ColdStart, [])))];
        }

        return Recommender.Rank(Taste, list, mode, Options);
    }

    /// <summary>Explanation templates of §6.9 in priority order: a place they liked, a creator, their categories, an alternative to a crowded place, cold start.</summary>
    public WhyDto Why(ScoredCandidate scored, PlaceInfo place, string destinationName)
    {
        if (!Control && Taste.Depth >= Options.ColdStartDepth)
        {
            var liked = Ratings
                .Where(r => r.Value >= 0.8 && r.Key != place.PoiId)
                .Select(r => (Poi: Places.FirstOrDefault(p => p.PoiId == r.Key), Rating: r.Value))
                .Where(x => x.Poi is not null)
                .Select(x => (x.Poi, Cosine: Diversification.Cosine(place.Weights, x.Poi!.Weights)))
                .Where(x => x.Cosine >= 0.7)
                .OrderByDescending(x => x.Cosine)
                .FirstOrDefault();
            if (liked.Poi is not null)
            {
                return new WhyDto("liked_similar", new Dictionary<string, string> { ["poiName"] = liked.Poi.Name });
            }
        }

        return scored.Reason.Code switch
        {
            ReasonCode.Categories => new WhyDto("categories", new Dictionary<string, string> { ["categories"] = string.Join(',', scored.Reason.Categories) }),
            ReasonCode.CreatorFollowed => new WhyDto("creator_followed", new Dictionary<string, string> { ["creator"] = scored.Reason.Creator ?? string.Empty }),
            ReasonCode.CreatorSimilar => new WhyDto("creator_similar", new Dictionary<string, string> { ["creator"] = scored.Reason.Creator ?? string.Empty }),
            ReasonCode.HiddenGem => new WhyDto("hidden_gem", new Dictionary<string, string>()),
            _ => new WhyDto("cold_start", new Dictionary<string, string> { ["destination"] = destinationName }),
        };
    }

    public RecommendationItemDto Item(ScoredCandidate scored, PlaceInfo place, string destinationName, bool exploration = false) =>
        new(place.PoiId, place.Slug, place.Name, Math.Round(scored.Score, 4), scored.CompatibilityPercent, Why(scored, place, destinationName), exploration, null);

    /// <summary>
    /// A list of <paramref name="count"/> places: the best after diversification, with the exploration share (20 %) taken from adjacent
    /// categories (§6.10), flagged so the app can say so.
    /// </summary>
    public IReadOnlyList<(ScoredCandidate Scored, PlaceInfo Place, bool Exploration)> Select(
        IReadOnlyList<ScoredCandidate> ranked, int count, IReadOnlyDictionary<(string A, string B), double>? lifts, int matrixTravelers)
    {
        var byId = Places.ToDictionary(p => p.PoiId.ToString("N"));
        var slots = Control ? 0 : Exploration.Slots(count);
        var main = Diversification.Apply(ranked, count - slots).ToList();

        var chosen = main.Select(s => (s, byId[s.Candidate.Id], false)).ToList();
        if (slots > 0)
        {
            var adjacent = Exploration.AdjacentCategories(Vector, lifts, matrixTravelers);
            var taken = main.Select(s => s.Candidate.Id).ToHashSet();
            var exploring = ranked
                .Where(s => !taken.Contains(s.Candidate.Id) && Exploration.DominantLeaf(s.Candidate.Weights) is { } leaf && adjacent.Contains(leaf))
                .Take(slots);
            chosen.AddRange(exploring.Select(s => (s, byId[s.Candidate.Id], true)));
        }

        // Fill what exploration could not (small catalogues, no adjacent category).
        if (chosen.Count < count)
        {
            var taken = chosen.Select(c => c.Item1.Candidate.Id).ToHashSet();
            chosen.AddRange(ranked.Where(s => !taken.Contains(s.Candidate.Id)).Take(count - chosen.Count).Select(s => (s, byId[s.Candidate.Id], false)));
        }

        // Exploration goes after the sure picks but not all at the very end: one every few positions.
        var explorations = chosen.Where(c => c.Item3).ToList();
        var sure = chosen.Where(c => !c.Item3).ToList();
        var merged = new List<(ScoredCandidate, PlaceInfo, bool)>(sure);
        var step = explorations.Count == 0 ? 0 : Math.Max(2, count / (explorations.Count + 1));
        for (var i = 0; i < explorations.Count; i++)
        {
            merged.Insert(Math.Min(merged.Count, step * (i + 1)), explorations[i]);
        }

        return merged;
    }
}
