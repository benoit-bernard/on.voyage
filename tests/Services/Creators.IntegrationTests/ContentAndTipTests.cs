using System.Net;
using System.Net.Http.Json;
using OnVoyage.Creators.Contracts;
using OnVoyage.TestInfrastructure;

namespace Creators.IntegrationTests;

/// <summary>Contents by URL, associations and tips entered by the administrator for a founding creator (T-1202), and the search of places.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ContentAndTipTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Base(Guid id) => $"/api/creators/v1/admin/creators/{id}";

    [Theory]
    [InlineData("https://youtu.be/{0}?si=abc", "youtube", "video")]
    [InlineData("https://www.instagram.com/reel/{1}/", "instagram", "video")]
    [InlineData("https://www.instagram.com/p/{1}/", "instagram", "photo")]
    [InlineData("https://www.tiktok.com/@marie/video/{2}", "tiktok", "video")]
    public async Task A_content_is_referenced_by_the_url_of_its_platform(string urlTemplate, string platform, string kind)
    {
        using var admin = _host.Admin();
        var creator = await _host.FounderAsync();
        var youtube = Guid.NewGuid().ToString("N")[..11];
        var instagram = Guid.NewGuid().ToString("N")[..11];
        var tiktok = string.Concat(Enumerable.Range(0, 18).Select(_ => Random.Shared.Next(0, 10)));
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, urlTemplate, youtube, instagram, tiktok);

        var added = await CreatorsHost.Read<AdminContentDto>(await admin.PostAsJsonAsync($"{Base(creator.Id)}/contents", new AddContentRequest(url, "Un titre", "Une légende", null, null, null, null, false, null), Ct), HttpStatusCode.Created);

        (added.Platform, added.Kind, added.Status).ShouldBe((platform, kind, "imported"));
        (await _host.Detail(creator.Id)).Contents.Single().Id.ShouldBe(added.Id);
    }

    [Fact]
    public async Task A_content_can_be_referenced_once_whoever_adds_it_and_an_unknown_url_is_refused()
    {
        using var admin = _host.Admin();
        var first = await _host.FounderAsync();
        var second = await _host.FounderAsync();
        var video = await _host.ContentAsync(first.Id);

        var duplicate = await admin.PostAsJsonAsync($"{Base(second.Id)}/contents", new AddContentRequest(video.Permalink + "&t=3s", "Même vidéo", null, null, null, null, null, false, null), Ct);
        var unknown = await admin.PostAsJsonAsync($"{Base(first.Id)}/contents", new AddContentRequest("https://vimeo.com/123456", "Autre plateforme", null, null, null, null, null, false, null), Ct);
        var untitled = await admin.PostAsJsonAsync($"{Base(first.Id)}/contents", new AddContentRequest("https://youtu.be/AAAAAAAAAAA", " ", null, null, null, null, null, false, null), Ct);
        var tooManyChapters = await admin.PostAsJsonAsync($"{Base(first.Id)}/contents", new AddContentRequest("https://youtu.be/BBBBBBBBBBB", "t", null, null, null, 100, null, false, [new ChapterDto(10, "a"), new ChapterDto(10, "b")]), Ct);

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync(Ct)).ShouldContain("invalid_content_url");
        untitled.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooManyChapters.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_chapters_of_a_video_are_kept_in_order()
    {
        var creator = await _host.FounderAsync();

        var video = await _host.ContentAsync(creator.Id, duration: 700, chapters: [new ChapterDto(340, "Roussillon"), new ChapterDto(135, "Gordes")]);

        video.Chapters.ShouldBe([new ChapterDto(135, "Gordes"), new ChapterDto(340, "Roussillon")]);
        (await _host.Detail(creator.Id)).Contents.Single().Chapters.Select(chapter => chapter.Title).ShouldBe(["Gordes", "Roussillon"]);
    }

    [Fact]
    public async Task An_association_is_validated_by_default_announced_once_and_changes_are_announced_both_ways()
    {
        using var admin = _host.Admin();
        var creator = await _host.FounderAsync();
        var poi = await _host.PoiAsync("Cours Julien");
        var video = await _host.ContentAsync(creator.Id, commercial: true);

        var link = await _host.LinkAsync(creator.Id, poi, video.Id);
        var again = await _host.LinkAsync(creator.Id, poi, video.Id); // the same association again changes nothing

        (link.Status, link.PoiName, link.ContentTitle).ShouldBe(("validated", "Cours Julien", video.Title));
        again.Id.ShouldBe(link.Id);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", "\"isCommercial\":true")).ShouldBeGreaterThanOrEqualTo(1);

        await admin.PutAsJsonAsync($"{Base(creator.Id)}/place-links/{link.Id}", new SetPlaceLinkStatusRequest("rejected"), Ct);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(2); // removed
        await admin.PutAsJsonAsync($"{Base(creator.Id)}/place-links/{link.Id}", new SetPlaceLinkStatusRequest("rejected"), Ct);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(2); // nothing new
        await admin.PutAsJsonAsync($"{Base(creator.Id)}/place-links/{link.Id}", new SetPlaceLinkStatusRequest("validated"), Ct);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(3);

        (await admin.DeleteAsync($"{Base(creator.Id)}/place-links/{link.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(4);
        (await admin.DeleteAsync($"{Base(creator.Id)}/place-links/{link.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_proposed_association_is_not_announced()
    {
        var creator = await _host.FounderAsync();
        var poi = await _host.PoiAsync("Parc Borély");

        var link = await _host.LinkAsync(creator.Id, poi, status: "proposed");

        (link.Status, link.ValidatedAt).ShouldBe(("proposed", null));
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(0);
    }

    [Fact]
    public async Task An_association_needs_a_known_place_a_content_of_the_same_creator_and_a_timestamp_inside_the_video()
    {
        using var admin = _host.Admin();
        var creator = await _host.FounderAsync();
        var other = await _host.FounderAsync();
        var poi = await _host.PoiAsync("Plage des Catalans");
        var video = await _host.ContentAsync(creator.Id, duration: 600);
        var foreign = await _host.ContentAsync(other.Id);

        async Task<HttpStatusCode> Try(AddPlaceLinkRequest request) => (await admin.PostAsJsonAsync($"{Base(creator.Id)}/place-links", request, Ct)).StatusCode;

        (await Try(new AddPlaceLinkRequest(Guid.NewGuid(), null, null, null))).ShouldBe(HttpStatusCode.NotFound);
        (await Try(new AddPlaceLinkRequest(poi, foreign.Id, null, null))).ShouldBe(HttpStatusCode.NotFound);
        (await Try(new AddPlaceLinkRequest(poi, video.Id, 601, null))).ShouldBe(HttpStatusCode.BadRequest);
        (await Try(new AddPlaceLinkRequest(poi, video.Id, -1, null))).ShouldBe(HttpStatusCode.BadRequest);
        (await Try(new AddPlaceLinkRequest(poi, null, 30, null))).ShouldBe(HttpStatusCode.BadRequest); // a timestamp needs a content
        (await Try(new AddPlaceLinkRequest(poi, video.Id, 30, "maybe"))).ShouldBe(HttpStatusCode.BadRequest);
        (await Try(new AddPlaceLinkRequest(poi, video.Id, 30, null))).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_tip_on_its_own_makes_the_creator_recommend_the_place_and_can_be_removed()
    {
        using var admin = _host.Admin();
        using var traveler = _host.Traveler();
        var creator = await _host.FounderAsync(publish: true);
        var poi = await _host.PoiAsync("Vallon des Auffes");

        var tip = await CreatorsHost.Read<AdminTipDto>(await admin.PutAsJsonAsync($"{Base(creator.Id)}/tips/{poi}", new SetTipRequest(" Venez au coucher du soleil, côté ouest. "), Ct));

        tip.Text.ShouldBe("Venez au coucher du soleil, côté ouest.");
        var detail = await _host.Detail(creator.Id);
        detail.PlaceLinks.Single().ShouldSatisfyAllConditions(link => link.ContentId.ShouldBeNull(), link => link.Status.ShouldBe("validated"), link => link.PoiName.ShouldBe("Vallon des Auffes"));
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(1);
        var block = await CreatorsHost.Read<PoiCreatorsDto>(await traveler.GetAsync($"/api/creators/v1/pois/{poi}/contents", Ct));
        (block.Items.Single().Tip, block.Items.Single().Content).ShouldBe(("Venez au coucher du soleil, côté ouest.", null));

        // Changing the tip does not announce the place again; removing it leaves the recommendation.
        await admin.PutAsJsonAsync($"{Base(creator.Id)}/tips/{poi}", new SetTipRequest("Mieux vaut y aller en semaine."), Ct);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(1);
        (await admin.DeleteAsync($"{Base(creator.Id)}/tips/{poi}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"{Base(creator.Id)}/tips/{poi}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CreatorsHost.Read<PoiCreatorsDto>(await traveler.GetAsync($"/api/creators/v1/pois/{poi}/contents", Ct))).Items.Single().Tip.ShouldBeNull();
    }

    [Fact]
    public async Task A_tip_is_280_characters_at_most_and_needs_a_known_place()
    {
        using var admin = _host.Admin();
        var creator = await _host.FounderAsync();
        var poi = await _host.PoiAsync("Les Goudes");

        (await admin.PutAsJsonAsync($"{Base(creator.Id)}/tips/{poi}", new SetTipRequest(new string('x', 281)), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"{Base(creator.Id)}/tips/{poi}", new SetTipRequest(new string('x', 280)), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"{Base(creator.Id)}/tips/{Guid.NewGuid()}", new SetTipRequest("ok"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Places_are_searched_by_part_of_a_name_ignoring_case_and_accents()
    {
        using var admin = _host.Admin();
        var tag = CreatorsHost.Unique("zq");
        await _host.PoiAsync($"Église Saint-Étienne {tag}", city: "Aix");
        await _host.PoiAsync($"Eglise des Réformés {tag}");
        await _host.PoiAsync($"Fort {tag} (retiré)", published: false);

        var byAccentedPrefix = await CreatorsHost.Read<List<PoiSearchResultDto>>(await admin.GetAsync($"/api/creators/v1/admin/places?query={Uri.EscapeDataString("ÉGLISE SAINT-ÉT")}", Ct));
        var byTag = await CreatorsHost.Read<List<PoiSearchResultDto>>(await admin.GetAsync($"/api/creators/v1/admin/places?query={tag}", Ct));
        var limited = await CreatorsHost.Read<List<PoiSearchResultDto>>(await admin.GetAsync($"/api/creators/v1/admin/places?query={tag}&limit=2", Ct));

        byAccentedPrefix.Select(item => item.Name).ShouldContain($"Église Saint-Étienne {tag}");
        byAccentedPrefix.ShouldAllBe(item => item.Name.Contains("Saint-Étienne", StringComparison.Ordinal));
        byTag.Select(item => item.Name).ShouldBe([$"Église Saint-Étienne {tag}", $"Eglise des Réformés {tag}", $"Fort {tag} (retiré)"], ignoreOrder: true);
        byTag.Last().IsPublished.ShouldBeFalse(); // withdrawn places come last
        limited.Count.ShouldBe(2);
        (await admin.GetAsync("/api/creators/v1/admin/places?query=a", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreatorsHost.Read<List<PoiSearchResultDto>>(await admin.GetAsync($"/api/creators/v1/admin/places?query={tag}&destination=nulle-part", Ct))).ShouldBeEmpty();
    }
}
