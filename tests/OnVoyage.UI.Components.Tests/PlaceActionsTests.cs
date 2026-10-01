using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Reports;
using OnVoyage.Catalog.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class PlaceActionsTests : BunitContext
{
    private static readonly Guid StoryId = Guid.NewGuid();
    private readonly IReportClient _reports = Substitute.For<IReportClient>();
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();

    private IRenderedComponent<Place> Arrange(IReadOnlyList<StoryDto>? stories = null)
    {
        var id = Guid.NewGuid();
        _catalog.GetPoiAsync("fort", Arg.Any<CancellationToken>()).Returns(new PoiDetailDto(
            id, "fort", "Fort Saint-Jean", "history", 43.29551, 5.36062, 0.8, 2, false,
            stories ?? [new StoryDto(StoryId, "fr", "Le fort", "Texte de l'histoire.", 90, null, true)],
            ["© OpenStreetMap contributors"],
            [new LinkDto("wikipedia", "fr", "Wikipédia", "https://fr.wikipedia.org/wiki/Fort_Saint-Jean", null, null, null)]));
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([]);
        var profiles = new InMemoryProfileStore();
        Services.AddSingleton<IProfileStore>(profiles);
        Services.AddSingleton(_catalog);
        this.AddLearning(profiles);
        Services.AddSingleton(_reports);
        return Render<Place>(p => p.Add(x => x.Slug, "fort"));
    }

    [Fact]
    public void The_go_there_link_opens_the_travelers_own_navigation_app()
    {
        var cut = Arrange();
        cut.WaitForAssertion(() => cut.Find("a.btn-soft").GetAttribute("href").ShouldBe("geo:43.29551,5.36062?q=43.29551,5.36062(Fort%20Saint-Jean)"));
    }

    [Fact]
    public void The_transcription_is_folded_by_default_and_external_links_are_announced()
    {
        var cut = Arrange();
        cut.WaitForAssertion(() => cut.Find("details.transcript").HasAttribute("open").ShouldBeFalse());
        cut.Find("details.transcript summary").TextContent.ShouldBe("Transcription de l'histoire");
        cut.Find(".learn-more .note").TextContent.ShouldContain("extérieurs à ON.VOYAGE");
    }

    [Fact]
    public void A_place_without_story_says_it_is_coming_soon()
    {
        var cut = Arrange([]);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Histoire bientôt disponible."));
    }

    [Fact]
    public void A_report_goes_to_factory_with_the_kind_and_the_message_and_never_a_position()
    {
        var cut = Arrange();
        cut.WaitForAssertion(() => cut.Find("details.report summary"));
        cut.Find("details.report summary").Click();
        cut.Find("select").Change(nameof(ReportKind.Pronunciation));
        cut.Find("textarea").Input("« Saint-Jean » se prononce autrement.");
        cut.Markup.ShouldContain("Votre position n'est pas envoyée.");
        cut.Find("details.report button").Click();

        _reports.Received(1).ReportAsync(StoryId, ReportKind.Pronunciation, "« Saint-Jean » se prononce autrement.", Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Merci, votre signalement a été transmis."));
    }

    [Fact]
    public void A_failed_report_keeps_the_message_and_tells_the_traveler()
    {
        _reports.ReportAsync(Arg.Any<Guid>(), Arg.Any<ReportKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new HttpRequestException("offline")));
        var cut = Arrange();
        cut.WaitForAssertion(() => cut.Find("details.report summary"));
        cut.Find("details.report summary").Click();
        cut.Find("textarea").Input("Fermé le lundi.");
        cut.Find("details.report button").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("Envoi impossible"));
        cut.Find("textarea").GetAttribute("value").ShouldBe("Fermé le lundi.");
    }

    [Fact]
    public void The_send_button_waits_for_a_message_and_the_message_is_capped_at_500_characters()
    {
        var cut = Arrange();
        cut.WaitForAssertion(() => cut.Find("details.report summary"));
        cut.Find("details.report summary").Click();
        cut.Find("details.report button").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("textarea").GetAttribute("maxlength").ShouldBe("500");
        ReportLabels.Compose(ReportKind.Other, new string('x', 900)).Length.ShouldBe(500);
        ReportLabels.Compose(ReportKind.Photo, "  ok ").ShouldBe("[Photo] ok");
    }
}
