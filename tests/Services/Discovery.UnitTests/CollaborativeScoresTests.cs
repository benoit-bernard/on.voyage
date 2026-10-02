using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.Discovery.Application;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.Recommendation.Engine;

namespace Discovery.UnitTests;

/// <summary>T-504: the periodic job of §6.5, with an in-memory store and the real exact cosine search.</summary>
public sealed class CollaborativeScoresTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Marseille1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Marseille2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Marseille3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid Arles1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Arles2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed class FakeStore(IReadOnlyList<CfTraveler> travelers, Dictionary<Guid, IReadOnlyDictionary<Guid, double>> ratings) : ICollaborativeStore
    {
        public Dictionary<Guid, IReadOnlyList<StoredCfScore>> Stored { get; } = [];

        public List<IReadOnlyCollection<Guid>> Kept { get; } = [];

        public int Removed { get; set; }

        public Task<IReadOnlyList<CfTraveler>> ActiveTravelersAsync(DateTimeOffset activeSince, int minDepth, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CfTraveler>>([.. travelers.Where(t => t.ProfileDepth >= minDepth)]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, double>>> NeighborRatingsAsync(NeighborFilter filter, CancellationToken cancellationToken)
        {
            var eligible = travelers.Where(t => t.ProfileDepth >= filter.MinDepth).Select(t => t.Id).ToHashSet();
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, double>>>(ratings.Where(pair => eligible.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        public Task<IReadOnlyDictionary<Guid, string>> PlaceDestinationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>
            {
                [Marseille1] = "marseille",
                [Marseille2] = "marseille",
                [Marseille3] = "marseille",
                [Arles1] = "arles",
                [Arles2] = "arles",
            });

        public Task ReplaceScoresAsync(Guid travelerId, IReadOnlyList<StoredCfScore> scores, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Stored[travelerId] = scores;
            return Task.CompletedTask;
        }

        public Task<int> DeleteScoresExceptAsync(IReadOnlyCollection<Guid> keepTravelerIds, CancellationToken cancellationToken)
        {
            Kept.Add(keepTravelerIds);
            return Task.FromResult(Removed);
        }
    }

    private sealed class InMemoryNeighbors(IReadOnlyList<CfTraveler> travelers) : INeighborSearch
    {
        public Task<int> PoolSizeAsync(NeighborFilter filter, CancellationToken cancellationToken) => Task.FromResult(Pool(filter).Count());

        public Task<IReadOnlyList<Neighbor>> NearestAsync(Guid travelerId, float[] vector, NeighborFilter filter, int k, CancellationToken cancellationToken) =>
            Task.FromResult(CollaborativeFiltering.Nearest(travelerId, vector, Pool(filter), k));

        private IEnumerable<NeighborCandidate> Pool(NeighborFilter filter) => travelers.Where(t => t.ProfileDepth >= filter.MinDepth).Select(t => new NeighborCandidate(t.Id, t.Vector));
    }

    private static Guid Person(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static CfTraveler HistoryFan(int n, int depth = 12) => new(Person(n), [1f, 0.1f, 0f], depth);

    private static CfTraveler NatureFan(int n, int depth = 12) => new(Person(n), [-0.2f, 0f, 1f], depth);

    private static Dictionary<Guid, IReadOnlyDictionary<Guid, double>> Rated(IEnumerable<CfTraveler> who, params (Guid Poi, double Rating)[] ratings) =>
        who.ToDictionary(t => t.Id, t => (IReadOnlyDictionary<Guid, double>)ratings.ToDictionary(r => r.Poi, r => r.Rating));

    private static async Task<(Result<CfRunSummary> Result, FakeStore Store)> RunAsync(IReadOnlyList<CfTraveler> travelers, Dictionary<Guid, IReadOnlyDictionary<Guid, double>> ratings, CfOptions? options = null)
    {
        var store = new FakeStore(travelers, ratings);
        var result = await RecomputeCfScoresHandler.Handle(
            new RecomputeCfScoresCommand(), store, new InMemoryNeighbors(travelers), options ?? new CfOptions(), new FakeTimeProvider(Now), NullLogger<RecomputeCfScoresCommand>.Instance, Ct);
        return (result, store);
    }

    [Fact]
    public async Task A_neighbour_with_the_same_taste_who_loved_a_place_gives_it_a_positive_score_with_its_support()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 22).Select(n => HistoryFan(n))];
        var me = HistoryFan(100);
        var dissidents = Enumerable.Range(200, 3).Select(n => NatureFan(n)).ToArray();
        var ratings = Rated(fans, (Marseille1, 1d), (Marseille2, 0.4d));
        foreach (var pair in Rated(dissidents, (Marseille1, -1d)))
        {
            ratings[pair.Key] = pair.Value;
        }

        var (result, store) = await RunAsync([.. fans, me, .. dissidents], ratings);

        result.Value!.Outcome.ShouldBe("computed");
        var mine = store.Stored[me.Id].ToDictionary(score => score.PoiId);
        mine[Marseille1].Score.ShouldBeGreaterThan(0.7d, "twenty-two similar travelers loved it; the three opposite ones are not neighbours");
        mine[Marseille1].Support.ShouldBe(22);
        mine[Marseille2].Score.ShouldBeLessThan(mine[Marseille1].Score);
        mine[Marseille1].Destination.ShouldBe("marseille");
        store.Stored[dissidents[0].Id].ShouldNotContain(score => score.PoiId == Marseille1 && score.Score > 0, "someone with other tastes does not inherit the history fans' enthusiasm");
    }

    [Fact]
    public async Task A_place_rated_by_only_two_neighbours_has_no_score_because_it_would_expose_what_they_said()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 24).Select(n => HistoryFan(n))];
        var ratings = Rated(fans.Take(2), (Marseille3, 1d));
        foreach (var pair in Rated(fans.Skip(2).Take(3), (Marseille1, 1d)))
        {
            ratings[pair.Key] = pair.Value;
        }

        var (_, store) = await RunAsync(fans, ratings);

        store.Stored.Values.SelectMany(scores => scores).ShouldNotContain(score => score.PoiId == Marseille3);
        store.Stored.Values.SelectMany(scores => scores).Where(score => score.PoiId == Marseille1).ShouldNotBeEmpty("three neighbours is enough");
    }

    [Fact]
    public async Task With_fewer_than_twenty_eligible_travelers_nothing_is_computed_and_what_was_stored_goes_away()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 19).Select(n => HistoryFan(n))];
        var store = new FakeStore(fans, Rated(fans, (Marseille1, 1d))) { Removed = 7 };

        var result = await RecomputeCfScoresHandler.Handle(new RecomputeCfScoresCommand(), store, new InMemoryNeighbors(fans), new CfOptions(), new FakeTimeProvider(Now), NullLogger<RecomputeCfScoresCommand>.Instance, Ct);

        result.Value!.Outcome.ShouldBe("pool_too_small");
        result.Value.Pool.ShouldBe(19);
        result.Value.Removed.ShouldBe(7);
        store.Stored.ShouldBeEmpty();
        store.Kept.ShouldHaveSingleItem().ShouldBeEmpty();
    }

    [Fact]
    public async Task Travelers_in_cold_start_get_no_score_and_do_not_count_as_neighbours()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 20).Select(n => HistoryFan(n))];
        var newcomer = HistoryFan(300, depth: 2);
        var shallow = HistoryFan(301, depth: 7);
        var ratings = Rated([.. fans, newcomer, shallow], (Marseille1, 1d));

        var (result, store) = await RunAsync([.. fans, newcomer, shallow], ratings);

        result.Value!.Pool.ShouldBe(20, "the eligible neighbours have a profile of at least 10");
        store.Stored.ShouldNotContainKey(newcomer.Id);
        store.Stored[shallow.Id].ShouldContain(score => score.PoiId == Marseille1, "past cold start the traveler gets scores");
        store.Stored[shallow.Id].Single(score => score.PoiId == Marseille1).Support.ShouldBe(20, "but is never a neighbour of anyone");
    }

    [Fact]
    public async Task Only_the_best_scores_of_each_destination_are_kept()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 22).Select(n => HistoryFan(n))];
        var me = HistoryFan(100);
        var ratings = Rated(fans, (Marseille1, 1d), (Marseille2, 0.5d), (Marseille3, -0.5d), (Arles1, 0.9d), (Arles2, 0.2d));

        var (_, store) = await RunAsync([.. fans, me], ratings, new CfOptions(MaxScoresPerTraveler: 2));

        var mine = store.Stored[me.Id];
        mine.Where(score => score.Destination == "marseille").Select(score => score.PoiId).ShouldBe([Marseille1, Marseille2]);
        mine.Where(score => score.Destination == "arles").Select(score => score.PoiId).ShouldBe([Arles1, Arles2]);
    }

    [Fact]
    public async Task The_same_data_gives_the_same_rows_whatever_the_order_the_travelers_come_in()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 30).Select(n => n % 3 == 0 ? NatureFan(n) : HistoryFan(n))];
        var ratings = Rated(fans, (Marseille1, 1d), (Marseille2, -0.4d));
        ratings[Person(5)] = new Dictionary<Guid, double> { [Marseille1] = -1d };

        var (_, first) = await RunAsync(fans, ratings);
        var (_, second) = await RunAsync([.. fans.Reverse()], ratings);

        first.Stored.Keys.Order().ShouldBe(second.Stored.Keys.Order());
        foreach (var (traveler, scores) in first.Stored)
        {
            scores.ShouldBe(second.Stored[traveler]);
        }
    }

    [Fact]
    public async Task Travelers_not_recomputed_lose_their_stored_scores()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 21).Select(n => HistoryFan(n))];
        var store = new FakeStore(fans, Rated(fans, (Marseille1, 1d))) { Removed = 4 };

        var result = await RecomputeCfScoresHandler.Handle(new RecomputeCfScoresCommand(), store, new InMemoryNeighbors(fans), new CfOptions(), new FakeTimeProvider(Now), NullLogger<RecomputeCfScoresCommand>.Instance, Ct);

        store.Kept.ShouldHaveSingleItem().Order().ShouldBe(fans.Select(f => f.Id).Order());
        result.Value!.Removed.ShouldBe(4);
        result.Value.Travelers.ShouldBe(21);
    }

    [Fact]
    public async Task A_strongly_disliked_place_gets_a_negative_score()
    {
        CfTraveler[] fans = [.. Enumerable.Range(1, 22).Select(n => HistoryFan(n))];
        var me = HistoryFan(100);

        var (_, store) = await RunAsync([.. fans, me], Rated(fans, (Marseille3, -1d)));

        store.Stored[me.Id].Single(score => score.PoiId == Marseille3).Score.ShouldBeLessThan(-0.7d);
    }
}
