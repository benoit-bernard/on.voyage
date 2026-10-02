using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Components.Pages;

namespace OnVoyage.Web.Admin.Tests;

public sealed class VideoPageTests : BunitContext
{
    private readonly IAdminApi _api = Substitute.For<IAdminApi>();

    public VideoPageTests()
    {
        Services.AddSingleton(_api);
        Services.AddSingleton(new AdminOptions("https://gateway.test"));
    }

    private static SelectedVideoItem Selected(string place, string destination, string id, string title) =>
        new(new PlaceVideoItem(id, title, "Chaîne", $"thumbs/x/{id}.jpg", $"https://www.youtube.com/watch?v={id}", new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), Guid.CreateVersion7()), place, destination);

    private static VideoQuotaItem Quota(int used, int daily = 10_000) =>
        new(new DateOnly(2026, 10, 2), used, daily, Math.Max(0, daily - used), Math.Max(0, daily - used) / 100, new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_overview_lists_every_selected_video_with_its_place_and_an_outgoing_link()
    {
        _api.ListSelectedVideosAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Selected("Fort Saint-Jean", "marseille", "abcdefghijk", "Le fort"), Selected("Gordes", "luberon", "ZYXWVUTSRQP", "Le village")]);
        _api.GetVideoQuotaAsync(Arg.Any<CancellationToken>()).Returns(Quota(300));

        var cut = Render<Videos>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Count.ShouldBe(2);
            cut.Find("tbody").TextContent.ShouldContain("Fort Saint-Jean");
            var link = cut.FindAll("tbody a[target=_blank]")[0];
            link.GetAttribute("href").ShouldBe("https://www.youtube.com/watch?v=abcdefghijk");
            link.GetAttribute("rel").ShouldBe("noopener noreferrer");
            cut.Markup.ShouldContain("2 vidéo(s) sur");
        });
    }

    [Fact]
    public void The_overview_can_be_narrowed_to_one_destination_and_a_video_can_be_removed()
    {
        var fort = Selected("Fort Saint-Jean", "marseille", "abcdefghijk", "Le fort");
        _api.ListSelectedVideosAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([fort, Selected("Gordes", "luberon", "ZYXWVUTSRQP", "Le village")]);
        _api.GetVideoQuotaAsync(Arg.Any<CancellationToken>()).Returns(Quota(0));
        var cut = Render<Videos>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(2));

        cut.Find("#destination").Change("luberon");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Count.ShouldBe(1);
            cut.Find("tbody").TextContent.ShouldContain("Gordes");
        });

        cut.Find("#destination").Change(string.Empty);
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(2));
        cut.FindAll("tbody button")[0].Click();
        _api.Received(1).RemoveVideoAsync(fort.Video.PlaceId, "abcdefghijk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_quota_line_says_what_is_left_and_when_it_resets()
    {
        _api.ListSelectedVideosAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Selected("Fort", "marseille", "abcdefghijk", "Le fort")]);
        _api.GetVideoQuotaAsync(Arg.Any<CancellationToken>()).Returns(Quota(2_350));

        var cut = Render<Videos>();

        cut.WaitForAssertion(() =>
        {
            var note = cut.Find("p.quota").TextContent;
            note.ShouldContain("2");
            note.ShouldContain("350 / 10");
            note.ShouldContain("76 recherche(s) possible(s)");
            note.ShouldContain("07:00 UTC");
            cut.Find("p.quota").ClassList.ShouldNotContain("bad");
        });
    }

    [Fact]
    public void A_full_quota_is_flagged_and_no_search_is_promised()
    {
        _api.ListSelectedVideosAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.GetVideoQuotaAsync(Arg.Any<CancellationToken>()).Returns(Quota(10_000));

        var cut = Render<Videos>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("p.quota").TextContent.ShouldContain("plus aucune recherche possible");
            cut.Find("p.quota").ClassList.ShouldContain("bad");
            cut.Markup.ShouldContain("Aucune vidéo sélectionnée");
        });
    }

    [Fact]
    public void Without_a_readable_quota_the_page_still_works()
    {
        _api.ListSelectedVideosAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Selected("Fort", "marseille", "abcdefghijk", "Le fort")]);
        _api.GetVideoQuotaAsync(Arg.Any<CancellationToken>()).Returns<Task<VideoQuotaItem>>(_ => throw new AdminApiException("down", 502));

        var cut = Render<Videos>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1));
        cut.FindAll("p.quota").ShouldBeEmpty();
        cut.FindAll("[role=alert]").ShouldBeEmpty();
    }

    [Fact]
    public void A_search_that_hits_the_quota_shows_the_reason_and_the_place_page_shows_the_quota()
    {
        var place = new PlaceItem(Guid.CreateVersion7(), "garde", "Notre-Dame de la Garde", null, 43.28, 5.37, null, "Candidate", 60, 50, false, "Rules", 1000, 0, null, []);
        _api.GetPlaceAsync(place.Id, Arg.Any<CancellationToken>()).Returns(new PlaceDetailItem(place, [], new EthicsItem(false, false), new CrowdItem(1, 2, 3), null, false));
        _api.ListVideosAsync(place.Id, Arg.Any<CancellationToken>()).Returns([]);
        _api.GetVideoQuotaAsync(Arg.Any<CancellationToken>()).Returns(Quota(9_950));
        _api.SearchVideosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VideoCandidateItem>>>(_ => throw new AdminApiException("The daily YouTube quota is used up (9950 of 10000 units).", 429));
        var cut = Render<PlaceDetail>(parameters => parameters.Add(p => p.Id, place.Id));
        cut.WaitForAssertion(() => cut.Find("p.quota").TextContent.ShouldContain("plus aucune recherche possible"));

        cut.Find("[aria-label='Recherche de vidéos']").Input("garde marseille");
        cut.FindAll("button").First(button => button.TextContent == "Chercher sur YouTube").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("quota is used up"));
    }
}
