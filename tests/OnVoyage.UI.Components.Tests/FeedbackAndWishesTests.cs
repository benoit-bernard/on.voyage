using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Feedback;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Planning;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Wishes;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.UI.Components.Pages;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

public sealed class FeedbackAndWishesTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
    private readonly SilentPlayer _player = new();
    private readonly InMemoryProfileStore _profiles = new();
    private readonly List<InteractionDto> _sent = [];
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly AudioPlaybackController _audio;
    private readonly PoiSummaryDto _fort;

    private sealed class Outbox(List<InteractionDto> sent) : IInteractionOutbox
    {
        public Task EnqueueAsync(InteractionDto interaction, CancellationToken cancellationToken)
        {
            sent.Add(interaction);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Notifier : ILocalNotifier
    {
        public Task NotifyAsync(string title, string body, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public FeedbackAndWishesTests()
    {
        _audio = new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), _clock);
        _fort = new PoiSummaryDto(Guid.NewGuid(), "fort-saint-nicolas", "Fort Saint-Nicolas", "history", 43.2905, 5.364, 0.8, 0.8, 2, false, null, 90,
            new Dictionary<string, double> { ["history"] = 0.9, ["history.military"] = 1d });
        _catalog.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns(new DestinationDto("marseille", "Marseille", 43.2965, 5.3698, 1));
        _catalog.GetPoisAsync("marseille", Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<CancellationToken>()).Returns([_fort]);

        var sessions = Substitute.For<ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, Guid.NewGuid(), true, null, []));
        Services.AddSingleton<ISessionProvider>(sessions);
        Services.AddSingleton<IProfileStore>(_profiles);
        Services.AddSingleton(_catalog);
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton(_audio);
        Services.AddSingleton<IInteractionOutbox>(new Outbox(_sent));
        Services.AddSingleton<ILocalNotifier>(new Notifier());
        Services.AddSingleton<InteractionRecorder>();
        Services.AddSingleton<FeedbackTracker>();
        Services.AddSingleton<ILocationProvider>(new NoLocationProvider());
        Services.AddScoped<DestinationService>();
        Services.AddSingleton<IReminderStore>(new InMemoryReminderStore());
        Services.AddSingleton<ILocationSource>(new SimulatedLocationSource());
        Services.AddSingleton<ProximityReminderService>();
    }

    private PlayRequest Story(PlayOrigin origin, string title = "Le Fort") =>
        new(Guid.NewGuid(), _fort.Id, title, [new AudioSource("https://m/a.mp3", AudioRole.Main)], origin, 90, _fort.Weights);

    private async Task Listen(PlayRequest request)
    {
        await _audio.PlayNowAsync(request, Ct);
        _player.Raise(new AudioPlayerEvent.Ended());
    }

    [Fact]
    public async Task The_banner_appears_after_a_story_and_a_like_sends_the_interaction()
    {
        var cut = Render<FeedbackBanner>();
        cut.Markup.Trim().ShouldBeEmpty();

        await cut.InvokeAsync(() => Listen(Story(PlayOrigin.Manual)));
        cut.Find("h2").TextContent.ShouldBe("Cette histoire vous a plu ?");

        cut.FindAll("button").Single(b => b.TextContent.Contains("J'ai aimé", StringComparison.Ordinal)).Click();
        _sent.ShouldContain(i => i.Kind == "like" && i.PoiId == _fort.Id);
        cut.Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public async Task Not_for_me_offers_the_place_or_the_category_and_defaults_to_the_place()
    {
        var cut = Render<FeedbackBanner>();
        await cut.InvokeAsync(() => Listen(Story(PlayOrigin.Manual)));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Pas pour moi", StringComparison.Ordinal)).Click();
        cut.Markup.ShouldContain("Ce lieu");
        cut.Markup.ShouldContain("Ce type de lieu (l'histoire)");
        cut.Find("input[type=radio]").HasAttribute("checked").ShouldBeTrue();

        cut.FindAll("button").Single(b => b.TextContent == "Valider").Click();
        _sent.ShouldContain(i => i.Kind == "dislike_poi");
    }

    [Fact]
    public async Task Choosing_the_category_sends_dislike_category()
    {
        var cut = Render<FeedbackBanner>();
        await cut.InvokeAsync(() => Listen(Story(PlayOrigin.Manual)));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Pas pour moi", StringComparison.Ordinal)).Click();
        cut.FindAll("input[type=radio]")[1].Change(true);
        cut.FindAll("button").Single(b => b.TextContent == "Valider").Click();

        _sent.Single(i => i.Kind == "dislike_category").CategoryCode.ShouldBe("history");
    }

    [Fact]
    public async Task The_banner_links_to_other_places_of_the_same_category()
    {
        var cut = Render<FeedbackBanner>();
        await cut.InvokeAsync(() => Listen(Story(PlayOrigin.Manual)));
        cut.Find("a.more").GetAttribute("href").ShouldBe("carte?categorie=history");
        cut.Find("a.more").TextContent.ShouldBe("D'autres lieux comme celui-ci");
    }

    [Fact]
    public async Task The_trip_recap_shows_from_two_unrated_stories_and_rates_in_one_tap()
    {
        var cut = Render<TripRecap>();
        await cut.InvokeAsync(() => Listen(Story(PlayOrigin.Discovery, "Fort")));
        cut.Markup.Trim().ShouldBeEmpty();

        await cut.InvokeAsync(() => Listen(Story(PlayOrigin.Discovery, "Calanque")));
        cut.FindAll("li").Count.ShouldBe(2);

        cut.Find("button[aria-label='J\\'ai aimé Fort']").Click();
        _sent.ShouldContain(i => i.Kind == "like");
        cut.Markup.Trim().ShouldBeEmpty(); // one left: no recap anymore
    }

    [Fact]
    public async Task The_wishes_screen_lists_saved_places_with_the_year_of_an_old_save_and_removes_them()
    {
        await _profiles.SaveAsync(new LocalProfile { Saved = [_fort.Id], SavedAt = new() { [_fort.Id] = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero) } }, Ct);
        var cut = Render<Wishes>();

        cut.WaitForAssertion(() => cut.Find("h2").TextContent.ShouldBe("Marseille"));
        cut.Find("li a").TextContent.ShouldContain("Fort Saint-Nicolas");
        cut.Find("li .meta").TextContent.ShouldBe("Ajouté en 2024");
        cut.Find("li a").GetAttribute("href").ShouldBe("lieu/fort-saint-nicolas");

        cut.Find("button.remove").Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Vous n'avez rien enregistré"));
        (await _profiles.LoadAsync(Ct)).Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_wishes_screen_is_empty_without_saved_places()
    {
        var cut = Render<Wishes>();
        await cut.WaitForAssertionAsync(() => cut.Markup.ShouldContain("Vous n'avez rien enregistré"));
    }

    [Fact]
    public async Task A_reminder_shows_as_a_banner_with_a_link_to_the_place()
    {
        await _profiles.SaveAsync(new LocalProfile { Saved = [_fort.Id], SavedAt = new() { [_fort.Id] = _clock.GetUtcNow().AddYears(-1) } }, Ct);
        var location = (SimulatedLocationSource)Services.GetRequiredService<ILocationSource>();
        await location.StartAsync(Ct);
        var cut = Render<ReminderBanner>();

        location.Emit(new LocationFix(43.2905 + (0.0045), 5.364, 10, 1.0, null, _clock.GetUtcNow()));
        await Services.GetRequiredService<ProximityReminderService>().LastHandlingForTests;

        cut.WaitForAssertion(() => cut.Find("p").TextContent.ShouldContain("Fort Saint-Nicolas"));
        cut.Find("a").GetAttribute("href").ShouldBe("lieu/fort-saint-nicolas");
        cut.Find("button").Click();
        cut.Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_destination_page_shows_the_profile_the_places_and_a_visit_plan()
    {
        await _profiles.SaveAsync(new LocalProfile { Affinities = new() { ["history"] = 0.8, ["nature"] = -0.5 }, Depth = 12 }, Ct);
        var many = Enumerable.Range(0, 30).Select(i => _fort with { Id = Guid.NewGuid(), Slug = $"p{i}", Name = $"Lieu {i}", Latitude = 43.29 + (i * 0.001), Longitude = 5.36 + ((i % 5) * 0.002) }).ToList();
        _catalog.GetPoisAsync("marseille", Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<CancellationToken>()).Returns(many);

        var cut = Render<Destination>();

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Marseille pour vous"));
        cut.FindAll(".bar").Count.ShouldBe(2); // one strong, one weak
        cut.Markup.ShouldContain("Moins pour vous");
        cut.FindAll("a.card").Count.ShouldBeGreaterThanOrEqualTo(1);
        cut.FindAll("a.card").Count.ShouldBeLessThanOrEqualTo(9);

        cut.Find("select[aria-label='Nombre de jours']").Change("2");
        cut.Find(".plan button").Click();
        cut.WaitForAssertion(() => cut.FindAll("article.day").Count.ShouldBe(2));
        cut.FindAll("article.day ol li").Count.ShouldBeInRange(8, 12);
    }
}
