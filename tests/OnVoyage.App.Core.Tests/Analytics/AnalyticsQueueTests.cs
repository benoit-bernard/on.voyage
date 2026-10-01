using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.Core.Analytics;
using OnVoyage.Insights.Contracts;

namespace OnVoyage.App.Core.Tests.Analytics;

internal sealed class FakeTransport : IAnalyticsTransport
{
    public List<IReadOnlyList<EventDto>> Batches { get; } = [];

    public AnalyticsSendOutcome Outcome { get; set; } = AnalyticsSendOutcome.Sent;

    public IEnumerable<EventDto> Sent => Batches.SelectMany(batch => batch);

    public Task<AnalyticsSendOutcome> SendAsync(IReadOnlyList<EventDto> batch, CancellationToken cancellationToken)
    {
        Batches.Add([.. batch]);
        return Task.FromResult(Outcome);
    }
}

public sealed class AnalyticsQueueTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeTransport _transport = new();
    private readonly AnalyticsConsent _consent = new();
    private readonly InMemoryAnalyticsStore _store = new();
    private readonly List<AnalyticsQueue> _queues = [];

    private AnalyticsQueue Queue(AnalyticsOptions? options = null)
    {
        var queue = new AnalyticsQueue(_transport, _consent, _store, new AnalyticsContext("1.2.3", Platforms.Android), _clock, options);
        _queues.Add(queue);
        return queue;
    }

    public void Dispose() => _queues.ForEach(queue => queue.Dispose());

    private static Dictionary<string, object?> Props(params (string Key, object? Value)[] items) => items.ToDictionary(item => item.Key, item => item.Value);

    [Fact]
    public async Task Consent_refused_means_only_the_essential_events_are_queued_and_sent()
    {
        _consent.Set(AnalyticsConsentState.Refused);
        var queue = Queue();

        queue.Track("app_open", Props(("cold_start", true)));
        queue.Track("audio_started", Props(("story_id", Guid.NewGuid())));
        queue.Track("poi_viewed", Props(("poi_id", "p1")));
        queue.Track("app_crash", Props(("code", "E_PLAYER")));
        queue.Track("audio_error", Props(("code", "decode")));
        queue.Track("gps_loss", None);
        queue.Pending.ShouldBe(3);
        await queue.FlushAsync(false, Ct);

        _transport.Sent.Select(e => e.Name).ShouldBe(["app_crash", "audio_error", "gps_loss"]);
        queue.Pending.ShouldBe(0);
        queue.Held.ShouldBe(0);
    }

    [Fact]
    public async Task Without_a_choice_the_non_essential_events_are_held_not_sent()
    {
        var queue = Queue();

        queue.Track("onboarding_started", None);
        queue.Track("app_crash", None);
        await queue.FlushAsync(false, Ct);

        queue.Held.ShouldBe(1);
        _transport.Sent.Select(e => e.Name).ShouldBe(["app_crash"]);
    }

    [Fact]
    public async Task Accepting_releases_the_held_events_in_order()
    {
        var queue = Queue();
        queue.Track("onboarding_started", None);
        _clock.Advance(TimeSpan.FromSeconds(5));
        queue.Track("onboarding_completed", Props(("answers_count", 5)));

        _consent.Set(AnalyticsConsentState.Granted);
        await queue.FlushAsync(false, Ct);

        _transport.Sent.Select(e => e.Name).ShouldBe(["onboarding_started", "onboarding_completed"]);
        queue.Held.ShouldBe(0);
    }

    [Fact]
    public async Task Refusing_throws_away_the_held_events_and_the_non_essential_ones_still_waiting()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue();
        _transport.Outcome = AnalyticsSendOutcome.Retry; // offline: they wait
        queue.Track("app_open", None);
        queue.Track("app_crash", None);
        await queue.FlushAsync(false, Ct);
        queue.Pending.ShouldBe(2);

        _consent.Set(AnalyticsConsentState.Refused);
        _transport.Outcome = AnalyticsSendOutcome.Sent;
        _transport.Batches.Clear();
        await queue.FlushAsync(true, Ct);

        _transport.Sent.Select(e => e.Name).ShouldBe(["app_crash"]);
    }

    [Fact]
    public async Task Refusing_during_the_onboarding_discards_what_was_held()
    {
        var queue = Queue();
        queue.Track("onboarding_started", None);

        _consent.Set(AnalyticsConsentState.Refused);
        await queue.FlushAsync(false, Ct);

        queue.Held.ShouldBe(0);
        _transport.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Events_go_out_in_batches_of_50()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue();
        for (var i = 0; i < 120; i++)
        {
            queue.Track("poi_viewed", Props(("poi_id", $"p{i}")));
        }

        await queue.FlushAsync(false, Ct);

        _transport.Batches.Select(batch => batch.Count).ShouldBe([50, 50, 20]);
        _transport.Sent.Select(e => e.Id).ShouldBeUnique();
    }

    [Fact]
    public async Task A_full_batch_is_sent_without_waiting_and_a_lone_event_after_a_minute()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue();

        queue.Track("app_open", None);
        _clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50, Ct);
        _transport.Batches.ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(2));
        await Eventually(() => _transport.Batches.Count == 1);
        _transport.Sent.Single().Name.ShouldBe("app_open");

        for (var i = 0; i < 50; i++)
        {
            queue.Track("poi_viewed", Props(("poi_id", "p")));
        }

        await Eventually(() => _transport.Batches.Count == 2);
        _transport.Batches[1].Count.ShouldBe(50);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20, Ct);
        }

        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task Offline_the_events_wait_back_off_and_are_resent_with_the_same_ids()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue();
        queue.Track("app_open", None);
        queue.Track("poi_viewed", Props(("poi_id", "p1")));
        _transport.Outcome = AnalyticsSendOutcome.Retry;

        await queue.FlushAsync(false, Ct);
        queue.Pending.ShouldBe(2);
        _transport.Batches.Count.ShouldBe(1);

        await queue.FlushAsync(false, Ct); // too soon: backing off
        _transport.Batches.Count.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await queue.FlushAsync(false, Ct);
        _transport.Batches.Count.ShouldBe(2);

        _clock.Advance(TimeSpan.FromSeconds(31)); // the wait has doubled to a minute
        await queue.FlushAsync(false, Ct);
        _transport.Batches.Count.ShouldBe(2);

        _transport.Outcome = AnalyticsSendOutcome.Sent;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await queue.FlushAsync(false, Ct);

        queue.Pending.ShouldBe(0);
        _transport.Batches[^1].Select(e => e.Id).ShouldBe(_transport.Batches[0].Select(e => e.Id));
    }

    [Fact]
    public async Task A_batch_the_server_refuses_for_good_is_dropped_so_it_does_not_block_the_next()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue(new AnalyticsOptions { BatchSize = 2 });
        for (var i = 0; i < 3; i++)
        {
            queue.Track("app_open", None);
        }

        _transport.Outcome = AnalyticsSendOutcome.Rejected;
        await queue.FlushAsync(false, Ct);

        queue.Pending.ShouldBe(0);
        _transport.Batches.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Waiting_events_survive_a_restart()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var first = Queue();
        first.Track("app_open", None);
        first.Track("app_crash", Props(("code", "E1")));
        _transport.Outcome = AnalyticsSendOutcome.Retry;
        await first.FlushAsync(false, Ct);
        var ids = _transport.Sent.Select(e => e.Id).ToList();

        _transport.Outcome = AnalyticsSendOutcome.Sent;
        _transport.Batches.Clear();
        var second = Queue(); // a new process: same store, nothing in memory
        await second.FlushAsync(false, Ct);

        _transport.Sent.Select(e => e.Id).ShouldBe(ids);
    }

    [Fact]
    public async Task A_saved_event_is_not_sent_if_the_consent_has_been_withdrawn_meanwhile()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var first = Queue();
        first.Track("app_open", None);
        first.Track("app_crash", None);
        _transport.Outcome = AnalyticsSendOutcome.Retry;
        await first.FlushAsync(false, Ct);

        _consent.Set(AnalyticsConsentState.Refused);
        _transport.Outcome = AnalyticsSendOutcome.Sent;
        _transport.Batches.Clear();
        await Queue().FlushAsync(false, Ct);

        _transport.Sent.Select(e => e.Name).ShouldBe(["app_crash"]);
    }

    [Fact]
    public async Task Only_the_catalogue_leaves_the_device_and_never_a_position()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue();

        queue.Track("secret_event", None);
        queue.Track("audio_speed_changed", Props(("speed", 1.25)));
        queue.Track("poi_viewed", Props(("poi_id", "p1"), ("surface", "map"), ("lat", 43.29), ("lon", 5.37), ("latitude", 43.29), ("device_id", "abc"), ("query", "fort")));
        queue.Track("search_performed", Props(("results_count", 12), ("text", "fort saint-jean")));
        queue.Track("recommendation_clicked", Props(("poi_id", "p1"), ("rank", 2), ("cohort", "beta"), ("is_exploration", "yes"), ("profile_depth", -4), ("weights_version", "w1")));
        await queue.FlushAsync(false, Ct);

        var sent = _transport.Sent.ToList();
        sent.Select(e => e.Name).ShouldBe(["poi_viewed", "search_performed", "recommendation_clicked"]);
        sent[0].Props!.Keys.ShouldBe(["poi_id", "surface"], ignoreOrder: true);
        sent[1].Props!.Keys.ShouldBe(["results_count"]);
        sent[2].Props!.Keys.ShouldBe(["poi_id", "rank", "weights_version"], ignoreOrder: true); // bad cohort, bad flag and negative depth are dropped, not the event
        sent.ShouldAllBe(e => e.AppVersion == "1.2.3" && e.Platform == Platforms.Android);
    }

    [Fact]
    public async Task What_the_app_already_reports_is_mapped_to_the_catalogue()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue();
        var story = Guid.NewGuid();

        queue.Track("story_triggered", Props(("poi_id", Guid.NewGuid().ToString()), ("mode", "walk"), ("distance_m", 130)));
        queue.Track("audio_started", Props(("story_id", story.ToString()), ("poi_id", Guid.NewGuid().ToString()), ("origin", "discovery")));
        queue.Track("audio_progress", Props(("story_id", story.ToString()), ("percent", 50)));
        await queue.FlushAsync(false, Ct);

        var sent = _transport.Sent.ToList();
        sent[0].Props!["distance_bucket_50m"].GetInt64().ShouldBe(2);
        sent[0].Props!.ContainsKey("distance_m").ShouldBeFalse();
        sent[1].Props!["trigger"].GetString().ShouldBe("auto");
        sent[1].Props!.ContainsKey("poi_id").ShouldBeFalse(); // not part of audio_started in the catalogue
        sent[2].Props!["percent"].GetDouble().ShouldBe(50);
    }

    [Fact]
    public async Task A_session_lasts_until_a_pause_of_half_an_hour()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue();

        queue.Track("app_open", None);
        _clock.Advance(TimeSpan.FromMinutes(10));
        queue.Track("poi_viewed", Props(("poi_id", "p")));
        _clock.Advance(TimeSpan.FromMinutes(31));
        queue.Track("app_open", None);
        await queue.FlushAsync(false, Ct);

        var sessions = _transport.Sent.Select(e => e.SessionId).ToList();
        sessions[0].ShouldBe(sessions[1]);
        sessions[2].ShouldNotBe(sessions[0]);
    }

    [Fact]
    public async Task The_queue_is_bounded_while_offline()
    {
        _consent.Set(AnalyticsConsentState.Granted);
        var queue = Queue(new AnalyticsOptions { MaxQueued = 10 });
        for (var i = 0; i < 25; i++)
        {
            queue.Track("poi_viewed", Props(("poi_id", $"p{i}")));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        queue.Pending.ShouldBe(10);
        await queue.FlushAsync(false, Ct);
        _transport.Sent.First().Props!["poi_id"].GetString().ShouldBe("p15"); // the oldest went first
    }
}
