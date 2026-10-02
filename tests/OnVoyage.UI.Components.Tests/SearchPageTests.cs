using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Search;
using OnVoyage.Catalog.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class SearchPageTests : BunitContext
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();

    public SearchPageTests() =>
        Services.AddScoped(_ => new SearchSession(_catalog, new NoOfflineSearch(), new InMemoryProfileStore(), new NullAnalyticsSink(), _clock));

    private static PoiSummaryDto Poi(string name) =>
        new(Guid.NewGuid(), name.ToLowerInvariant().Replace(' ', '-'), name, "religion", 43.3, 5.36, 0.7, 0.8, 3, false, null, 90, new Dictionary<string, double> { ["religion"] = 1d });

    [Fact]
    public void The_field_is_labelled_and_explains_what_to_type()
    {
        var cut = Render<SearchPage>();

        cut.Find("label[for=search-input]").TextContent.ShouldBe("Rechercher un lieu");
        cut.Find("input[type=search]").GetAttribute("autocomplete").ShouldBe("off");
        cut.Find("#search-status").TextContent.ShouldContain("fort");
    }

    [Fact]
    public void One_character_asks_for_two_and_sends_nothing()
    {
        var cut = Render<SearchPage>();

        cut.Find("input").Input("c");
        _clock.Advance(TimeSpan.FromSeconds(2));

        cut.Find("#search-status").TextContent.ShouldBe("Tapez au moins 2 caractères.");
        _catalog.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void Typing_then_pausing_lists_the_results_as_links_to_the_places()
    {
        _catalog.SearchAsync("marseille", "cathedrale", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Poi("Cathédrale de la Major"), Poi("Abbaye Saint-Victor")]);
        var cut = Render<SearchPage>();

        cut.Find("input").Input("cathedrale");
        _clock.Advance(SearchSession.Debounce);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".results li a").Select(a => a.GetAttribute("href")).ShouldBe(["lieu/cathédrale-de-la-major", "lieu/abbaye-saint-victor"]);
            cut.Find("#search-status").TextContent.ShouldBe("2 résultat(s).");
        });
    }

    [Fact]
    public void Submitting_searches_without_waiting()
    {
        _catalog.SearchAsync("marseille", "fort", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Poi("Fort Saint-Jean")]);
        var cut = Render<SearchPage>();
        cut.Find("input").Input("fort");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.FindAll(".results li").Count.ShouldBe(1));
    }

    [Fact]
    public void No_match_says_so_with_the_typed_text()
    {
        _catalog.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var cut = Render<SearchPage>();

        cut.Find("input").Input("zzzz");
        _clock.Advance(SearchSession.Debounce);

        cut.WaitForAssertion(() => cut.Find("#search-status").TextContent.ShouldBe("Aucun lieu ne correspond à « zzzz »."));
    }

    [Fact]
    public void Without_network_and_pack_the_page_points_to_offline_packs()
    {
        _catalog.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<PoiSummaryDto>>(_ => throw new HttpRequestException());
        var cut = Render<SearchPage>();

        cut.Find("input").Input("fort");
        _clock.Advance(SearchSession.Debounce);

        cut.WaitForAssertion(() => cut.Find("#search-status").TextContent.ShouldContain("pack hors ligne"));
    }
}
