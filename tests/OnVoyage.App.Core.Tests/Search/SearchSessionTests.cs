#pragma warning disable xUnit1051 // driven against in-memory fakes
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Search;
using OnVoyage.App.Core.Tests.Audio;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Core.Tests.Search;

public sealed class SearchSessionTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly IOfflineSearch _offline = Substitute.For<IOfflineSearch>();
    private readonly RecordingAnalytics _analytics = new();
    private readonly SearchSession _session;

    public SearchSessionTests() => _session = new SearchSession(_catalog, _offline, new InMemoryProfileStore(), _analytics, _clock);

    public void Dispose() => _session.Dispose();

    private static PoiSummaryDto Poi(string name) =>
        new(Guid.NewGuid(), name.ToLowerInvariant().Replace(' ', '-'), name, "religion", 43.3, 5.36, 0.7, 0.8, 3, false, null, 90, new Dictionary<string, double> { ["religion"] = 1d });

    private async Task SettleAsync()
    {
        _clock.Advance(SearchSession.Debounce);
        await _session.Pending;
    }

    [Fact]
    public async Task Under_two_characters_nothing_is_requested()
    {
        _session.OnInput("c");
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _session.Pending;

        _session.State.Status.ShouldBe(SearchStatus.TooShort);
        await _catalog.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default, default);

        _session.OnInput("  ");
        _session.State.Status.ShouldBe(SearchStatus.Idle);
    }

    [Fact]
    public async Task Keystrokes_within_300_ms_make_a_single_request_with_the_final_text()
    {
        _catalog.SearchAsync("marseille", "cathedrale", SearchSession.ResultLimit, Arg.Any<CancellationToken>()).Returns([Poi("Cathédrale de la Major")]);

        _session.OnInput("ca");
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        _session.OnInput("cathe");
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        _session.OnInput("cathedrale");
        await _catalog.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default, default);

        await SettleAsync();

        await _catalog.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _session.State.Status.ShouldBe(SearchStatus.Results);
        _session.State.Results.Single().Name.ShouldBe("Cathédrale de la Major");
        _session.State.FromPack.ShouldBeFalse();
    }

    [Fact]
    public async Task The_search_waits_for_the_pause_not_less()
    {
        _catalog.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _session.OnInput("fort");

        _clock.Advance(TimeSpan.FromMilliseconds(299));
        await _catalog.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default, default);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await _session.Pending;
        await _catalog.Received(1).SearchAsync("marseille", "fort", SearchSession.ResultLimit, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_text_is_trimmed_and_collapsed_and_enter_searches_at_once()
    {
        _catalog.SearchAsync("marseille", "fort saint", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Poi("Fort Saint-Jean")]);

        await _session.SearchNowAsync("  fort   saint ");

        _session.State.Results.Count.ShouldBe(1);
        _session.State.Text.ShouldBe("fort saint");
    }

    [Fact]
    public async Task An_answer_that_arrives_after_a_newer_search_is_dropped()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<PoiSummaryDto>>();
        _catalog.SearchAsync("marseille", "port", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(slow.Task);
        _catalog.SearchAsync("marseille", "fort", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Poi("Fort Saint-Jean")]);

        var first = _session.SearchNowAsync("port");
        await _session.SearchNowAsync("fort");
        slow.SetResult([Poi("Vieux-Port")]);
        await first;

        _session.State.Text.ShouldBe("fort");
        _session.State.Results.Single().Name.ShouldBe("Fort Saint-Jean");
    }

    [Fact]
    public async Task Without_network_the_pack_answers_and_says_so()
    {
        _catalog.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());
        _offline.SearchAsync("marseille", "major", SearchSession.ResultLimit, Arg.Any<CancellationToken>()).Returns([Poi("Cathédrale de la Major")]);

        await _session.SearchNowAsync("major");

        _session.State.Status.ShouldBe(SearchStatus.Results);
        _session.State.FromPack.ShouldBeTrue();
    }

    [Fact]
    public async Task A_timeout_counts_as_no_network()
    {
        _catalog.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TaskCanceledException("timeout"));
        _offline.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Poi("Cathédrale de la Major")]);

        await _session.SearchNowAsync("major");

        _session.State.FromPack.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_network_and_without_pack_the_search_fails_visibly()
    {
        _catalog.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());
        _offline.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PoiSummaryDto>?)null);

        await _session.SearchNowAsync("major");

        _session.State.Status.ShouldBe(SearchStatus.Failed);
    }

    [Fact]
    public async Task No_match_is_an_empty_state_and_the_event_carries_the_count_only()
    {
        _catalog.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await _session.SearchNowAsync("zzzz secret text");

        _session.State.Status.ShouldBe(SearchStatus.Empty);
        var tracked = _analytics.Events.Single();
        tracked.Name.ShouldBe("search_performed");
        tracked.Properties.ShouldBe(new Dictionary<string, object?> { ["results_count"] = 0 });
    }
}
