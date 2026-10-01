using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.Insights.Application.Features.Events;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Contracts;

namespace Insights.UnitTests;

public sealed class IngestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private static readonly IngestEventsValidator Validator = new(Clock);
    private static readonly Guid Traveler = Guid.NewGuid();

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static EventDto Event(string name = "app_open", Dictionary<string, JsonElement>? props = null, TimeSpan? age = null) =>
        new(Guid.NewGuid(), name, Clock.GetUtcNow() - (age ?? TimeSpan.FromMinutes(5)), Guid.NewGuid(), "1.0.0", Platforms.Android, props);

    private static string Check(params EventDto[] events)
    {
        var result = Validator.Validate(new IngestEventsCommand(Traveler, events));
        return result.IsValid ? string.Empty : result.Errors[0].ErrorMessage;
    }

    [Fact]
    public void The_catalogue_has_the_three_essential_events_and_no_property_that_could_hold_a_position()
    {
        EventCatalogue.EssentialNames.ShouldBe(["app_crash", "audio_error", "gps_loss"], ignoreOrder: true);
        EventCatalogue.All.Select(e => e.Name).ShouldBeUnique();

        string[] forbidden = ["lat", "lon", "lng", "coord", "position", "location", "gps", "device", "advertis", "idfa", "gaid", "imei", "email"];
        EventCatalogue.All.SelectMany(e => e.Properties).Select(p => p.Name)
            .ShouldAllBe(name => !forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void A_correct_event_passes() => Check(Event("recommendation_clicked", new()
    {
        ["poi_id"] = J("b0a1"),
        ["surface"] = J("home"),
        ["rank"] = J(2),
        ["cohort"] = J("personalized"),
        ["weights_version"] = J("w1"),
        ["is_exploration"] = J(false),
        ["profile_depth"] = J(12),
    })).ShouldBeEmpty();

    [Fact]
    public void An_empty_batch_is_refused() => Check().ShouldContain("at least one");

    [Fact]
    public void Unknown_events_and_properties_are_refused()
    {
        Check(Event("ad_click")).ShouldContain("unknown event");
        Check(Event("app_open", new() { ["latitude"] = J(43.3) })).ShouldContain("latitude");
        Check(Event("poi_viewed", new() { ["lat"] = J(43.3), ["lon"] = J(5.3) })).ShouldContain("not part of event");
        Check(Event("search_performed", new() { ["query"] = J("fort") })).ShouldContain("query");
    }

    [Fact]
    public void Property_values_must_match_the_catalogue()
    {
        Check(Event("recommendation_viewed", new() { ["cohort"] = J("beta") })).ShouldContain("invalid value");
        Check(Event("audio_progress", new() { ["percent"] = J(140) })).ShouldContain("invalid value");
        Check(Event("audio_progress", new() { ["percent"] = J("50") })).ShouldContain("invalid value");
        Check(Event("audio_progress", new() { ["trigger"] = J("remote") })).ShouldContain("invalid value");
        Check(Event("poi_viewed", new() { ["poi_id"] = J(new string('x', 65)) })).ShouldContain("invalid value");
        Check(Event("search_performed", new() { ["results_count"] = J(-1) })).ShouldContain("invalid value");
        Check(Event("search_performed", new() { ["results_count"] = J(1.5) })).ShouldContain("invalid value");
        Check(Event("app_open", new() { ["cold_start"] = J("yes") })).ShouldContain("invalid value");
        Check(Event("app_open", new() { ["cold_start"] = default })).ShouldContain("invalid value");
    }

    [Fact]
    public void Envelope_fields_are_checked()
    {
        Check(Event() with { Id = Guid.Empty }).ShouldContain("id");
        Check(Event() with { SessionId = Guid.Empty }).ShouldContain("sessionId");
        Check(Event() with { Platform = "windows" }).ShouldContain("platform");
        Check(Event() with { AppVersion = "" }).ShouldContain("appVersion");
        Check(Event() with { OccurredAt = Clock.GetUtcNow().AddHours(1) }).ShouldContain("future");
    }

    private sealed class FakeStore(bool consent) : IEventStore
    {
        public List<NewEvent> Stored { get; } = [];

        public Task<bool> HasAnalyticsConsentAsync(Guid travelerId, CancellationToken cancellationToken) => Task.FromResult(consent);

        public Task<int> InsertAsync(IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
        {
            Stored.AddRange(events);
            return Task.FromResult(events.Count);
        }
    }

    private sealed class FakeSettings(InsightsSettings? settings = null) : IInsightsSettings
    {
        public Task<InsightsSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings ?? new InsightsSettings());
    }

    private static Task<OnVoyage.Insights.Application.Result<EventBatchResponse>> Ingest(FakeStore store, IReadOnlyList<EventDto> events, InsightsSettings? settings = null) =>
        IngestEventsHandler.Handle(new IngestEventsCommand(Traveler, events), Validator, store, new FakeSettings(settings), Clock, Ct);

    [Fact]
    public async Task Without_the_statistics_consent_only_essential_events_are_kept()
    {
        var store = new FakeStore(consent: false);

        var result = await Ingest(store, [Event("app_open"), Event("audio_started"), Event("app_crash", new() { ["code"] = J("E42") }), Event("gps_loss")]);

        result.Value!.ShouldBe(new EventBatchResponse(2, 0, 2));
        store.Stored.Select(e => e.Name).ShouldBe(["app_crash", "gps_loss"]);
    }

    [Fact]
    public async Task With_the_consent_everything_is_kept_and_the_traveler_comes_from_the_token()
    {
        var store = new FakeStore(consent: true);

        var result = await Ingest(store, [Event("app_open"), Event("audio_started"), Event("app_crash")]);

        result.Value!.ShouldBe(new EventBatchResponse(3, 0, 0));
        store.Stored.ShouldAllBe(e => e.TravelerId == Traveler);
    }

    [Fact]
    public async Task An_event_repeated_in_one_batch_is_stored_once_and_counted_as_a_duplicate()
    {
        var store = new FakeStore(consent: true);
        var one = Event();

        var result = await Ingest(store, [one, one with { }]);

        result.Value!.ShouldBe(new EventBatchResponse(1, 1, 0));
    }

    [Fact]
    public async Task Events_older_than_their_retention_are_ignored_not_refused()
    {
        var store = new FakeStore(consent: true);

        // 14 months for a usage event (retention 13 months), 100 days for a technical one (retention 90 days).
        var result = await Ingest(store, [Event("app_open", age: TimeSpan.FromDays(430)), Event("app_crash", age: TimeSpan.FromDays(100)), Event("app_open", age: TimeSpan.FromDays(100))]);

        result.Value!.ShouldBe(new EventBatchResponse(1, 0, 2));
        store.Stored.Single().Name.ShouldBe("app_open");
    }

    [Fact]
    public async Task A_batch_over_the_configured_size_is_refused()
    {
        var store = new FakeStore(consent: true);
        var events = Enumerable.Range(0, 51).Select(_ => Event()).ToList();

        (await Ingest(store, events, new InsightsSettings(MaxEventsPerBatch: 50))).IsSuccess.ShouldBeFalse();
        (await Ingest(store, events[..50], new InsightsSettings(MaxEventsPerBatch: 50))).IsSuccess.ShouldBeTrue();
        (await Ingest(store, [.. Enumerable.Range(0, 500).Select(_ => Event())])).IsSuccess.ShouldBeTrue();
        store.Stored.Count.ShouldBe(550);
    }

    [Fact]
    public async Task One_bad_event_refuses_the_whole_batch_and_stores_nothing()
    {
        var store = new FakeStore(consent: true);

        var result = await Ingest(store, [Event(), Event("poi_viewed", new() { ["lat"] = J(1) })]);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("validation");
        store.Stored.ShouldBeEmpty();
    }
}
