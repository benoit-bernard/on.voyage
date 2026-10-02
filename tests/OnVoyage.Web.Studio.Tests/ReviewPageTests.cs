using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Creators.Contracts;
using OnVoyage.Web.Studio.Api;
using OnVoyage.Web.Studio.Auth;
using OnVoyage.Web.Studio.Components.Pages;

namespace OnVoyage.Web.Studio.Tests;

/// <summary>T-1209: the screen where the creator validates, rejects or corrects what the assistant found in their contents.</summary>
public sealed class ReviewPageTests : BunitContext
{
    private static readonly Guid Content = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IStudioApi _api = Substitute.For<IStudioApi>();

    public ReviewPageTests()
    {
        Services.AddSingleton(_api);
        var auth = AddAuthorization();
        auth.SetAuthorized("marie@onvoyage.test");
        auth.SetClaims(new Claim(StudioClaims.Roles, "creator"));
    }

    private static PlaceProposalDto Item(string name, double confidence, string destination = "provence", int? start = null, params string[] signals) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, "Luberon", destination, Content, "Provence en 3 jours",
            $"https://www.youtube.com/watch?v=abcdefghijk{(start is { } seconds ? $"&t={seconds}s" : string.Empty)}", start, confidence, $"… {name} …", signals);

    private static PlaceProposalsDto Proposals(params PlaceProposalDto[] items) =>
        new(items.Length, items.Count(item => item.Confidence >= 0.9), 0.9, 0,
            [.. items.GroupBy(item => item.DestinationSlug).Select(group => new PlaceProposalGroupDto(group.Key, [.. group]))]);

    private IRenderedComponent<Review> Show(PlaceProposalsDto proposals)
    {
        _api.GetProposalsAsync(Arg.Any<CancellationToken>()).Returns(proposals);
        var cut = Render<Review>();
        cut.WaitForAssertion(() => cut.Find("#headline"));
        return cut;
    }

    [Fact]
    public void The_screen_says_how_many_places_were_found_and_groups_them_by_destination_with_the_chapter_link()
    {
        var cut = Show(Proposals(Item("Gordes", 1d, start: 135, signals: "chapter"), Item("Roussillon", 0.95, start: 340), Item("Vieux-Port", 0.97, "marseille")));

        cut.Find("#headline").TextContent.ShouldBe("Nous avons trouvé 3 lieux dans vos contenus");
        cut.FindAll(".group").Select(group => group.Id).ShouldBe(["group-provence", "group-marseille"]);
        var gordes = cut.FindAll("tr.proposal").Single(row => row.TextContent.Contains("Gordes", StringComparison.Ordinal));
        gordes.QuerySelector("a")!.GetAttribute("href").ShouldBe("https://www.youtube.com/watch?v=abcdefghijk&t=135s");
        gordes.TextContent.ShouldContain("à 02:15");
        gordes.QuerySelector(".confidence")!.TextContent.ShouldContain("100 %");
        cut.Find("p.muted").TextContent.ShouldContain("Rien n'est publié sans votre accord");
    }

    [Fact]
    public void Tout_valider_counts_the_proposals_from_ninety_percent_and_sends_the_threshold()
    {
        _api.ValidateProposalsAsync(null, 0.9, Arg.Any<CancellationToken>()).Returns(new ReviewResultDto(2, 0));
        var cut = Show(Proposals(Item("Gordes", 1d), Item("Roussillon", 0.93), Item("Lourmarin", 0.6)));
        cut.Find("#validate-all").TextContent.ShouldContain("Tout valider (2 à 90 % ou plus)");
        _api.GetProposalsAsync(Arg.Any<CancellationToken>()).Returns(Proposals(Item("Lourmarin", 0.6)));

        cut.Find("#validate-all").Click();

        cut.WaitForAssertion(() => _api.Received(1).ValidateProposalsAsync(null, 0.9, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("2 lieu(x) validé(s)"));
        cut.WaitForAssertion(() => cut.Find("#headline").TextContent.ShouldBe("Nous avons trouvé 1 lieu dans vos contenus"));
        cut.Find("#validate-all").HasAttribute("disabled").ShouldBeTrue(); // nothing left above the threshold
    }

    [Fact]
    public void A_proposal_below_the_threshold_is_flagged_for_review_and_is_validated_one_by_one()
    {
        var unsure = Item("Notre-Dame de la Garde", 0.72, "marseille", signals: ["partial", "ambiguous"]);
        _api.ValidateProposalsAsync(Arg.Any<IReadOnlyList<Guid>?>(), null, Arg.Any<CancellationToken>()).Returns(new ReviewResultDto(1, 0));
        var cut = Show(Proposals(unsure));
        var row = cut.Find("tr.proposal");
        row.ClassList.ShouldContain("review");
        row.QuerySelector(".confidence")!.TextContent.ShouldContain("72 %");
        row.QuerySelector(".confidence")!.TextContent.ShouldContain("à vérifier");
        row.QuerySelector(".confidence")!.TextContent.ShouldContain("nom ambigu");
        cut.Find("#validate-all").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("button.accept").Click();

        cut.WaitForAssertion(() => _api.Received(1).ValidateProposalsAsync(Arg.Is<IReadOnlyList<Guid>?>(ids => ids != null && ids.SequenceEqual(new[] { unsure.LinkId })), null, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("Notre-Dame de la Garde est validé"));
    }

    [Fact]
    public void A_proposal_is_rejected_for_good()
    {
        var item = Item("Quechua", 0.5);
        _api.RejectProposalsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>()).Returns(new ReviewResultDto(0, 1));
        var cut = Show(Proposals(item));

        cut.Find("button.reject").Click();

        cut.WaitForAssertion(() => _api.Received(1).RejectProposalsAsync(Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { item.LinkId })), Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("ne sera plus proposé"));
    }

    [Fact]
    public void The_creator_corrects_the_place_by_searching_the_right_one()
    {
        var wrong = Item("Notre-Dame de Paris", 0.7, "paris");
        var right = new PoiSearchResultDto(Guid.NewGuid(), "Notre-Dame de la Garde", "Marseille", "marseille", true);
        _api.SearchPlacesAsync("garde", Arg.Any<CancellationToken>()).Returns([right]);
        _api.CorrectProposalAsync(wrong.LinkId, right.PoiId, Arg.Any<CancellationToken>()).Returns(new AdminPlaceLinkDto(Guid.NewGuid(), right.PoiId, right.Name, Content, "Provence en 3 jours", null, 1d, "validated", null));
        var cut = Show(Proposals(wrong));

        cut.Find("button.correct").Click();
        cut.Find("#correct-search").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#correct-query").Input("garde");
        cut.Find("#correct-search").Click();
        cut.WaitForAssertion(() => cut.Find("#correct-results").TextContent.ShouldContain("Notre-Dame de la Garde"));
        cut.Find("#correct-results button").Click();

        cut.WaitForAssertion(() => _api.Received(1).CorrectProposalAsync(wrong.LinkId, right.PoiId, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("le lieu est corrigé et validé"));
        cut.FindAll("tr.correction").ShouldBeEmpty();
    }

    [Fact]
    public void Without_proposals_the_screen_says_so_and_offers_the_analysis_and_counts_contents_not_analysed()
    {
        _api.AnalyzeAsync(false, Arg.Any<CancellationToken>()).Returns(new AnalysisRequestedDto(3));
        var cut = Show(new PlaceProposalsDto(0, 0, 0.9, 3, []));

        cut.Find("#headline").TextContent.ShouldBe("Aucune proposition en attente");
        cut.Find("#pending").TextContent.ShouldContain("3 contenu(s) pas encore analysé(s)");
        cut.FindAll("#validate-all").ShouldBeEmpty();

        cut.Find("#analyze").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("Analyse demandée pour 3 contenu(s)"));
        cut.Find("#reanalyze").Click();
        cut.WaitForAssertion(() => _api.Received(1).AnalyzeAsync(true, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void A_refusal_of_the_service_is_shown_as_it_is()
    {
        _api.ValidateProposalsAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns<ReviewResultDto>(_ => throw new StudioApiException("Votre espace est suspendu : contactez l'équipe ON.VOYAGE.", 403));
        var cut = Show(Proposals(Item("Gordes", 1d)));

        cut.Find("#validate-all").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("suspendu"));
    }
}
