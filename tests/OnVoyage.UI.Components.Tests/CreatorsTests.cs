using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Creators;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Creators.Contracts;
using OnVoyage.Discovery.Contracts;
using OnVoyage.UI.Components.Pages;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

public sealed class CreatorsTests : BunitContext
{
    private static readonly Guid Poi = Guid.CreateVersion7();
    private readonly ICreatorsClient _client = Substitute.For<ICreatorsClient>();
    private readonly RecordingOutbox _outbox = new();
    private readonly InMemoryProfileStore _profiles = new();

    public CreatorsTests()
    {
        this.AddLearning(_profiles);
        this.AddCreators(_client, _profiles, _outbox);
        var catalog = Substitute.For<ICatalogClient>();
        catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([
            new PoiSummaryDto(Poi, "fort-saint-jean", "Fort Saint-Jean", "history", 43.29, 5.36, 0.8, 0.8, 2, false, null, null, new Dictionary<string, double> { ["history"] = 1d }),
        ]);
        Services.AddSingleton(catalog);
    }

    private static CreatorSummaryDto Summary(string handle, string name = "") =>
        new(Guid.NewGuid(), handle, name.Length == 0 ? handle : name, $"avatars/{handle}.jpg", ["history"], 3);

    private static PoiCreatorItemDto Item(CreatorSummaryDto creator, string? tip = null, string? url = null, bool commercial = false, int? seconds = 92) =>
        new(creator, tip, url is null ? null : new CreatorContentDto(Guid.NewGuid(), "youtube", "video", $"Vidéo de {creator.Handle}", url, 92, seconds, null, commercial, null));

    private void Block(int total, params PoiCreatorItemDto[] items)
    {
        _client.GetPoiCreatorsAsync(Poi, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PoiCreatorsDto(Poi, total, items));
    }

    private void Affinities(params (CreatorSummaryDto Creator, int Affinity)[] values) =>
        _client.GetCreatorsForMeAsync("marseille", Arg.Any<CancellationToken>()).Returns(new CreatorsForMeDto(
            "marseille", [.. values.Select(v => new CreatorForMeDto(v.Creator.Id, v.Creator.Handle, v.Creator.DisplayName, v.Creator.AvatarPath, v.Creator.Specialties, 3, v.Affinity, false))], "personalized"));

    private IRenderedComponent<CreatorsBlock> RenderBlock() =>
        Render<CreatorsBlock>(parameters => parameters.Add(p => p.PoiId, Poi).Add(p => p.Weights, new Dictionary<string, double> { ["history"] = 1d }));

    [Fact]
    public void The_block_shows_three_creators_by_affinity_with_a_link_out_that_keeps_the_chapter_timestamp()
    {
        var a = Summary("anna");
        var b = Summary("bruno");
        var c = Summary("clara");
        var d = Summary("dario");
        Block(
            4,
            Item(a, url: "https://www.youtube.com/watch?v=abcdefghijk&t=92s"),
            Item(b, tip: "Allez-y au lever du soleil.", url: "https://www.instagram.com/reel/XYZ/"),
            Item(c),
            Item(d, url: "https://www.youtube.com/watch?v=zzzzzzzzzzz"));
        Affinities((a, 20), (b, 90), (c, 55), (d, 10));

        var cut = RenderBlock();

        cut.WaitForAssertion(() =>
        {
            var handles = cut.FindAll(".handle").Select(h => h.TextContent).ToArray();
            handles.ShouldBe(["@bruno", "@clara", "@anna"]); // by affinity, three at most
            cut.Find("h2").TextContent.ShouldBe("Vu par les créateurs");
            cut.Find("[data-creator=anna] .see").GetAttribute("href").ShouldBe("https://www.youtube.com/watch?v=abcdefghijk&t=92s");
            cut.Find("[data-creator=anna] .meta").TextContent.ShouldContain("Vidéo · 1:32");
            cut.Find("[data-creator=bruno] .tip").TextContent.ShouldContain("lever du soleil");
            cut.Find("[data-creator=anna] .handle").GetAttribute("href").ShouldBe("createur/anna");
            cut.Find("[data-creator=anna] img").GetAttribute("src").ShouldBe("https://media.test/media/avatars/anna.jpg");
        });
        cut.FindAll(".see").ShouldAllBe(a => a.GetAttribute("target") == "_blank" && a.GetAttribute("rel") == "noopener noreferrer");
        cut.FindAll("iframe, embed, object, video, script").ShouldBeEmpty();
    }

    [Fact]
    public void Only_content_marked_as_advertising_carries_the_label()
    {
        var paid = Summary("paid");
        var free = Summary("free");
        Block(2, Item(paid, url: "https://www.youtube.com/watch?v=aaaaaaaaaaa", commercial: true), Item(free, url: "https://www.youtube.com/watch?v=bbbbbbbbbbb"));
        Affinities((paid, 50), (free, 40));

        var cut = RenderBlock();

        cut.WaitForAssertion(() => cut.FindAll(".creator").Count.ShouldBe(2));
        cut.FindAll(".ad").Count.ShouldBe(1);
        cut.Find("[data-creator=paid] .ad").TextContent.ShouldBe("Publicité");
        cut.FindAll("[data-creator=free] .ad").ShouldBeEmpty();
    }

    [Fact]
    public void Opening_a_content_records_creator_content_opened_on_the_place_and_nothing_about_the_creator_is_sent_to_the_server_but_the_place()
    {
        var a = Summary("anna");
        Block(1, Item(a, url: "https://www.youtube.com/watch?v=abcdefghijk"));
        Affinities((a, 50));
        var cut = RenderBlock();
        cut.WaitForAssertion(() => cut.FindAll(".see").Count.ShouldBe(1));

        cut.Find(".see").Click();

        var sent = _outbox.Items.ShouldHaveSingleItem();
        sent.Kind.ShouldBe("creator_content_opened");
        sent.PoiId.ShouldBe(Poi);
    }

    [Fact]
    public void All_creators_shows_the_whole_list_and_the_order_survives_a_failing_discovery()
    {
        var creators = Enumerable.Range(0, 5).Select(i => Summary($"c{i}")).ToArray();
        Block(5, [.. creators.Select(c => Item(c))]);
        _client.GetCreatorsForMeAsync("marseille", Arg.Any<CancellationToken>()).Returns<Task<CreatorsForMeDto?>>(_ => throw new HttpRequestException("down"));
        var cut = RenderBlock();
        cut.WaitForAssertion(() => cut.FindAll(".creator").Count.ShouldBe(3));
        cut.FindAll(".handle").Select(h => h.TextContent).ShouldBe(["@c0", "@c1", "@c2"]);

        cut.Find(".more").Click();

        cut.FindAll(".creator").Count.ShouldBe(5);
        cut.FindAll(".more").ShouldBeEmpty();
    }

    [Fact]
    public void Without_creators_or_when_the_service_fails_the_block_is_absent()
    {
        _client.GetPoiCreatorsAsync(Poi, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PoiCreatorsDto(Poi, 0, []));
        RenderBlock().FindAll(".creators").ShouldBeEmpty();

        _client.GetPoiCreatorsAsync(Poi, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns<Task<PoiCreatorsDto?>>(_ => throw new HttpRequestException("down"));
        RenderBlock().FindAll(".creators").ShouldBeEmpty();
    }

    [Fact]
    public void A_content_can_be_reported_with_a_reason_from_the_closed_list()
    {
        var a = Summary("anna");
        var item = Item(a, url: "https://www.youtube.com/watch?v=abcdefghijk");
        Block(1, item);
        Affinities((a, 50));
        _client.ReportAsync(Arg.Any<ReportRequest>(), Arg.Any<CancellationToken>()).Returns(new ReportReceiptDto(Guid.NewGuid()));
        var cut = RenderBlock();
        cut.WaitForAssertion(() => cut.FindAll(".creator").Count.ShouldBe(1));

        cut.Find(".report summary").Click();
        cut.Find(".report select").Change(ReportReasons.UndeclaredAd);
        cut.Find(".report button").Click();

        cut.WaitForAssertion(() => cut.Find(".report [role=status]").TextContent.ShouldContain("Merci"));
        _client.Received(1).ReportAsync(Arg.Is<ReportRequest>(r => r.TargetType == "content" && r.TargetId == item.Content!.Id && r.Reason == "undeclared_ad"), Arg.Any<CancellationToken>());
        cut.FindAll(".report textarea, .report input[type=text]").ShouldBeEmpty(); // no free text (F-33)
    }

    private static CreatorPageDto Page(bool following = false, int? followers = null, string handle = "marie") => new(
        Guid.NewGuid(), handle, "Marie Dupont", "Voyageuse et historienne.", "avatars/marie.jpg", ["fr"], ["history", "food"],
        [new CreatorLinkDto("youtube", "https://www.youtube.com/@marie")], followers, followers is null, 1, 1, following,
        [new CreatorPlaceDto(Poi, "Fort Saint-Jean", "Marseille", "Montez au coucher du soleil.",
            [new CreatorContentDto(Guid.NewGuid(), "youtube", "video", "Le fort en 90 secondes", "https://www.youtube.com/watch?v=abcdefghijk&t=30s", 30, 90, null, true, null)])],
        [], ["youtube"]);

    [Fact]
    public void The_creator_page_shows_the_profile_the_validated_places_and_links_out_only()
    {
        _client.GetCreatorAsync("marie", Arg.Any<CancellationToken>()).Returns(Page());

        var cut = Render<CreatorPage>(parameters => parameters.Add(p => p.Handle, "marie"));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Marie Dupont"));
        cut.Find(".handle").TextContent.ShouldBe("@marie");
        cut.Find(".stats").TextContent.ShouldContain("Nouveau créateur");
        cut.Find("#verified").TextContent.ShouldBe("Comptes vérifiés : YouTube");
        cut.Find(".place h3 a").GetAttribute("href").ShouldBe("lieu/fort-saint-jean");
        cut.Find(".place .tip").TextContent.ShouldContain("coucher du soleil");
        cut.Find(".place .ad").TextContent.ShouldBe("Publicité");
        cut.Find(".place .content a").GetAttribute("href").ShouldBe("https://www.youtube.com/watch?v=abcdefghijk&t=30s");
        cut.FindAll("a[href^='http']").ShouldAllBe(a => a.GetAttribute("target") == "_blank" && a.GetAttribute("rel") == "noopener noreferrer");
        cut.FindAll("iframe, embed, object, video, script").ShouldBeEmpty();
    }

    [Fact]
    public void A_creator_with_enough_followers_shows_their_number()
    {
        _client.GetCreatorAsync("marie", Arg.Any<CancellationToken>()).Returns(Page(followers: 1234));

        var cut = Render<CreatorPage>(parameters => parameters.Add(p => p.Handle, "@marie"));

        cut.WaitForAssertion(() => cut.Find(".stats").TextContent.ShouldContain("abonnés"));
        cut.Find(".stats").TextContent.ShouldNotContain("Nouveau créateur");
    }

    [Fact]
    public void Following_and_unfollowing_call_the_service_and_flip_the_pressed_state()
    {
        var page = Page();
        _client.GetCreatorAsync("marie", Arg.Any<CancellationToken>()).Returns(page);
        _client.SetFollowAsync(page.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call => new FollowStateDto(page.Id, call.ArgAt<bool>(1)));
        var cut = Render<CreatorPage>(parameters => parameters.Add(p => p.Handle, "marie"));
        cut.WaitForAssertion(() => cut.Find(".actions button").GetAttribute("aria-pressed").ShouldBe("false"));

        cut.Find(".actions button").Click();
        cut.WaitForAssertion(() => cut.Find(".actions button").GetAttribute("aria-pressed").ShouldBe("true"));
        cut.Find(".actions button").Click();
        cut.WaitForAssertion(() => cut.Find(".actions button").GetAttribute("aria-pressed").ShouldBe("false"));

        Received.InOrder(() =>
        {
            _client.SetFollowAsync(page.Id, true, Arg.Any<CancellationToken>());
            _client.SetFollowAsync(page.Id, false, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void A_failed_follow_keeps_the_state_and_says_so()
    {
        var page = Page();
        _client.GetCreatorAsync("marie", Arg.Any<CancellationToken>()).Returns(page);
        _client.SetFollowAsync(page.Id, true, Arg.Any<CancellationToken>()).Returns<Task<FollowStateDto?>>(_ => throw new HttpRequestException("down"));
        var cut = Render<CreatorPage>(parameters => parameters.Add(p => p.Handle, "marie"));
        cut.WaitForAssertion(() => cut.FindAll(".actions button").Count.ShouldBe(1));

        cut.Find(".actions button").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("Réessayez"));
        cut.Find(".actions button").GetAttribute("aria-pressed").ShouldBe("false");
    }

    [Fact]
    public void A_creator_can_be_reported_from_their_page_and_an_unknown_handle_says_not_found()
    {
        var page = Page();
        _client.GetCreatorAsync("marie", Arg.Any<CancellationToken>()).Returns(page);
        _client.ReportAsync(Arg.Any<ReportRequest>(), Arg.Any<CancellationToken>()).Returns(new ReportReceiptDto(Guid.NewGuid()));
        var cut = Render<CreatorPage>(parameters => parameters.Add(p => p.Handle, "marie"));
        cut.WaitForAssertion(() => cut.FindAll(".report summary").Count.ShouldBe(1));

        cut.Find(".report summary").Click();
        cut.Find(".report select").Change(ReportReasons.Impersonation);
        cut.Find(".report button").Click();

        cut.WaitForAssertion(() => cut.Find(".report [role=status]").TextContent.ShouldContain("Merci"));
        _client.Received(1).ReportAsync(Arg.Is<ReportRequest>(r => r.TargetType == "creator" && r.TargetId == page.Id && r.Reason == "impersonation"), Arg.Any<CancellationToken>());

        var missing = Render<CreatorPage>(parameters => parameters.Add(p => p.Handle, "nobody"));
        missing.WaitForAssertion(() => missing.Find(".empty").TextContent.ShouldContain("introuvable"));
    }

    private sealed class RecordingOutbox : IInteractionOutbox
    {
        public List<InteractionDto> Items { get; } = [];

        public Task EnqueueAsync(InteractionDto interaction, CancellationToken cancellationToken)
        {
            Items.Add(interaction);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
