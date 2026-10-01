#pragma warning disable xUnit1051 // in-memory fakes: nothing here waits
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Feedback;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Tests.Audio;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.App.Core.Tests;

public sealed class FeedbackTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryProfileStore _profiles = new();
    private readonly CapturingOutbox _outbox = new();
    private readonly CapturingNotifier _notifier = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly AudioPlaybackController _audio;
    private readonly FeedbackTracker _tracker;

    private sealed class CapturingOutbox : IInteractionOutbox
    {
        public List<InteractionDto> Sent { get; } = [];

        public Task EnqueueAsync(InteractionDto interaction, CancellationToken cancellationToken)
        {
            Sent.Add(interaction);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingNotifier : ILocalNotifier
    {
        public List<string> Bodies { get; } = [];

        public Task NotifyAsync(string title, string body, CancellationToken cancellationToken)
        {
            Bodies.Add(body);
            return Task.CompletedTask;
        }
    }

    public FeedbackTests()
    {
        _audio = new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), _clock);
        var recorder = new InteractionRecorder(_profiles, _outbox, _clock);
        _tracker = new FeedbackTracker(_audio, recorder, _notifier, _clock);
    }

    public void Dispose() => _tracker.Dispose();

    private static readonly IReadOnlyDictionary<string, double> History = new Dictionary<string, double> { ["history"] = 0.9, ["history.military"] = 1.0 };

    private static PlayRequest Story(string title, PlayOrigin origin) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, [new AudioSource("https://m/a.mp3", AudioRole.Main)], origin, 90, History);

    private async Task Listen(PlayRequest request)
    {
        await _audio.PlayNowAsync(request, Ct);
        _player.Raise(new AudioPlayerEvent.Ended());
    }

    [Fact]
    public async Task A_story_heard_on_request_asks_for_a_reaction_and_a_like_is_recorded_and_learned()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await Listen(story);

        _tracker.Banner!.Title.ShouldBe("Le Fort");
        await _tracker.RateAsync(story.StoryId, FeedbackChoice.Liked, cancellationToken: Ct);

        _tracker.Banner.ShouldBeNull();
        var like = _outbox.Sent.Single(i => i.Kind == "like");
        like.PoiId.ShouldBe(story.PoiId);
        like.StoryId.ShouldBe(story.StoryId);
        (await _profiles.LoadAsync(Ct)).Affinities["history.military"].ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Not_for_me_on_the_place_excludes_it_and_barely_moves_the_category()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await Listen(story);
        await _tracker.RateAsync(story.StoryId, FeedbackChoice.NotForMe, NotForMeScope.Place, Ct);

        _outbox.Sent.Single(i => i.Kind == "dislike_poi").PoiId.ShouldBe(story.PoiId);
        var profile = await _profiles.LoadAsync(Ct);
        profile.Excluded.ShouldContain(story.PoiId);
        profile.Affinities["history.military"].ShouldBe(0.15 * -0.18 * 1.0, 1e-9);
    }

    [Fact]
    public async Task Not_for_me_on_the_category_does_not_exclude_the_place_and_pushes_the_category_down()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await Listen(story);
        await _tracker.RateAsync(story.StoryId, FeedbackChoice.NotForMe, NotForMeScope.Category, Ct);

        var sent = _outbox.Sent.Single(i => i.Kind == "dislike_category");
        sent.CategoryCode.ShouldBe("history");
        var profile = await _profiles.LoadAsync(Ct);
        profile.Excluded.ShouldBeEmpty();
        profile.Affinities["history"].ShouldBe(-0.15, 1e-9);
    }

    [Fact]
    public async Task Meh_is_a_small_negative_signal()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await Listen(story);
        await _tracker.RateAsync(story.StoryId, FeedbackChoice.Meh, cancellationToken: Ct);

        _outbox.Sent.ShouldContain(i => i.Kind == "meh");
        (await _profiles.LoadAsync(Ct)).Affinities["history.military"].ShouldBeLessThan(0);
    }

    [Fact]
    public async Task A_story_skipped_before_the_end_asks_nothing()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await _audio.PlayNowAsync(story, Ct);
        await _audio.StopAsync();

        _tracker.Banner.ShouldBeNull();
        _tracker.Unrated.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stories_of_the_discovery_mode_pile_up_and_two_make_a_trip_recap_with_one_notification()
    {
        await Listen(Story("Fort", PlayOrigin.Discovery));
        _tracker.RecapAvailable.ShouldBeFalse();
        _tracker.Banner.ShouldBeNull();
        _notifier.Bodies.ShouldBeEmpty();

        await Listen(Story("Calanque", PlayOrigin.Discovery));
        _tracker.RecapAvailable.ShouldBeTrue();
        _notifier.Bodies.ShouldHaveSingleItem().ShouldContain("2 histoires");

        await Listen(Story("Panier", PlayOrigin.Discovery));
        _notifier.Bodies.Count.ShouldBe(1); // told once, not at every story
    }

    [Fact]
    public async Task Rating_from_the_recap_removes_the_story_and_closes_the_recap_below_two()
    {
        var first = Story("Fort", PlayOrigin.Discovery);
        await Listen(first);
        await Listen(Story("Calanque", PlayOrigin.Discovery));

        await _tracker.RateAsync(first.StoryId, FeedbackChoice.Liked, cancellationToken: Ct);

        _tracker.RecapAvailable.ShouldBeFalse();
        _tracker.Unrated.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Dismissing_the_recap_forgets_the_unrated_stories()
    {
        await Listen(Story("Fort", PlayOrigin.Discovery));
        await Listen(Story("Calanque", PlayOrigin.Discovery));
        _tracker.DismissRecap();
        _tracker.Unrated.ShouldBeEmpty();
        _outbox.Sent.Where(i => i.Kind is "like" or "meh" or "dislike_poi").ShouldBeEmpty();
    }

    [Fact]
    public async Task Listening_signals_are_sent_without_asking_anything()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await _audio.PlayNowAsync(story, Ct);
        _player.Raise(new AudioPlayerEvent.PositionChanged(TimeSpan.FromSeconds(85), TimeSpan.FromSeconds(100)));

        _outbox.Sent.ShouldContain(i => i.Kind == "listen_80");
    }

    [Fact]
    public async Task Dropping_a_story_in_its_first_seconds_is_an_early_abandon()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await _audio.PlayNowAsync(story, Ct);
        _player.Raise(new AudioPlayerEvent.PositionChanged(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(100)));
        await _audio.NextAsync();

        _outbox.Sent.ShouldContain(i => i.Kind == "abandon_early");
    }

    [Fact]
    public async Task Every_recorded_interaction_has_its_own_event_id_and_no_coordinates()
    {
        var story = Story("Le Fort", PlayOrigin.Manual);
        await Listen(story);
        await _tracker.RateAsync(story.StoryId, FeedbackChoice.Liked, cancellationToken: Ct);

        _outbox.Sent.Select(i => i.ClientEventId).Distinct().Count().ShouldBe(_outbox.Sent.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(_outbox.Sent);
        json.ShouldNotContain("atitude");
        json.ShouldNotContain("ongitude");
    }
}
