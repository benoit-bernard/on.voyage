using OnVoyage.Discovery.Application.Features;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Discovery.Domain;

namespace Discovery.UnitTests;

public sealed class OnboardingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClipCandidate Clip(string category, double quality, double importance = 0.5, string? sub = null)
    {
        var weights = new Dictionary<string, double> { [category] = 0.9 };
        if (sub is not null)
        {
            weights[$"{category}.{sub}"] = 1.0;
        }

        return new ClipCandidate(Guid.NewGuid(), Guid.NewGuid(), "fr", weights, importance, quality);
    }

    private static List<ClipCandidate> Pool() =>
    [
        Clip("history", 0.9, sub: "military"), Clip("history", 0.95, sub: "maritime"),
        Clip("nature", 0.8, sub: "coast"), Clip("nature", 0.7),
        Clip("culture", 0.85, sub: "contemporary_art"), Clip("gastronomy", 0.6), Clip("religion", 0.75), Clip("architecture", 0.5), Clip("leisure", 0.4),
    ];

    [Fact]
    public void The_selection_holds_five_clips_from_five_different_level_one_categories()
    {
        var picked = OnboardingSelector.Select(Pool());

        picked.Count.ShouldBe(5);
        OnboardingSelector.AreDistinct(picked).ShouldBeTrue();
    }

    [Fact]
    public void Per_category_the_best_clip_wins_and_the_best_categories_are_kept()
    {
        var pool = Pool();
        var picked = OnboardingSelector.Select(pool);

        picked.ShouldContain(pool[1]); // history 0.95 over 0.9
        picked.ShouldNotContain(pool[0]);
        picked.ShouldNotContain(pool[8]); // leisure 0.4 is the weakest category
    }

    [Fact]
    public void Selection_is_deterministic_whatever_the_order_of_the_candidates()
    {
        var pool = Pool();
        var first = OnboardingSelector.Select(pool).Select(c => c.StoryId).Order().ToArray();
        var second = OnboardingSelector.Select(Enumerable.Reverse(pool)).Select(c => c.StoryId).Order().ToArray();
        second.ShouldBe(first);
    }

    [Fact]
    public void With_fewer_categories_than_five_it_returns_what_exists()
    {
        OnboardingSelector.Select([Clip("history", 0.9), Clip("history", 0.8), Clip("nature", 0.7)]).Count.ShouldBe(2);
    }

    [Fact]
    public void Two_clips_of_the_same_category_are_not_distinct()
    {
        OnboardingSelector.AreDistinct([Clip("history", 0.9), Clip("history", 0.8)]).ShouldBeFalse();
    }

    private sealed class FakeOnboardingStore(List<StoredClip> clips) : IOnboardingStore
    {
        public IReadOnlyList<Guid>? Active { get; private set; }

        public Task<IReadOnlyList<StoredClip>> ListAsync(string? lang, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoredClip>>(clips);

        public Task<bool> SetActiveAsync(IReadOnlyList<Guid> storyIds, CancellationToken cancellationToken)
        {
            Active = storyIds;
            return Task.FromResult(true);
        }
    }

    private sealed class Media : IMediaUrls
    {
        public string Url(string path) => "https://media/" + path;
    }

    private static FakeOnboardingStore StoreOf(IEnumerable<ClipCandidate> pool) =>
        new([.. pool.Select(c => new StoredClip(c.StoryId, c.PoiId, "fr", "t", "clips/a.mp3", 15, false, c))]);

    [Fact]
    public async Task Without_an_editor_choice_the_endpoint_serves_five_distinct_categories()
    {
        var result = await GetOnboardingClipsHandler.Handle(new GetOnboardingClipsQuery("fr"), StoreOf(Pool()), new Media(), Ct);

        result.Value!.Count.ShouldBe(5);
        result.Value.Select(c => c.Category).Distinct().Count().ShouldBe(5);
        result.Value[0].AudioUrl.ShouldStartWith("https://media/");
    }

    [Fact]
    public async Task The_editor_choice_replaces_the_automatic_one()
    {
        var pool = Pool();
        var store = new FakeOnboardingStore([.. pool.Select((c, i) => new StoredClip(c.StoryId, c.PoiId, "fr", "t", "a.mp3", 15, i is 0 or 2 or 4 or 5 or 6, c))]);
        var result = await GetOnboardingClipsHandler.Handle(new GetOnboardingClipsQuery("fr"), store, new Media(), Ct);
        result.Value!.Select(c => c.StoryId).Order().ShouldBe(new[] { pool[0], pool[2], pool[4], pool[5], pool[6] }.Select(c => c.StoryId).Order());
    }

    [Fact]
    public async Task The_admin_cannot_activate_two_clips_of_one_category_or_fewer_than_five()
    {
        var pool = Pool();
        var store = StoreOf(pool);

        var twoSame = await SetActiveClipsHandler.Handle(new SetActiveClipsCommand([pool[0].StoryId, pool[1].StoryId, pool[2].StoryId, pool[4].StoryId, pool[5].StoryId]), store, Ct);
        twoSame.IsSuccess.ShouldBeFalse();
        twoSame.Error!.Message.ShouldContain("five different level-1 categories");

        (await SetActiveClipsHandler.Handle(new SetActiveClipsCommand([pool[0].StoryId, pool[2].StoryId]), store, Ct)).IsSuccess.ShouldBeFalse();

        var ok = await SetActiveClipsHandler.Handle(new SetActiveClipsCommand([pool[0].StoryId, pool[2].StoryId, pool[4].StoryId, pool[5].StoryId, pool[6].StoryId]), store, Ct);
        ok.IsSuccess.ShouldBeTrue();
        store.Active!.Count.ShouldBe(5);
    }

    [Fact]
    public void Onboarding_events_get_stable_ids_so_a_resend_stores_once()
    {
        var traveler = Guid.NewGuid();
        SubmitOnboardingHandler.DeterministicId(traveler, "clip", "abc").ShouldBe(SubmitOnboardingHandler.DeterministicId(traveler, "clip", "abc"));
        SubmitOnboardingHandler.DeterministicId(traveler, "clip", "abc").ShouldNotBe(SubmitOnboardingHandler.DeterministicId(traveler, "clip", "abd"));
        SubmitOnboardingHandler.DeterministicId(traveler, "clip", "abc").ShouldNotBe(SubmitOnboardingHandler.DeterministicId(Guid.NewGuid(), "clip", "abc"));
    }
}
