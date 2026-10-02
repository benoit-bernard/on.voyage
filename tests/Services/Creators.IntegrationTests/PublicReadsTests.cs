using System.Net;
using System.Net.Http.Json;
using OnVoyage.Creators.Contracts;
using OnVoyage.TestInfrastructure;

namespace Creators.IntegrationTests;

/// <summary>What travelers can see (F-26, F-28, F-30): only a validated link of a published creator to a published place, with its content online.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PublicReadsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<CreatorPageDto?> PageAsync(string handle)
    {
        using var traveler = _host.Traveler();
        var response = await traveler.GetAsync($"/api/creators/v1/creators/{handle}", Ct);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await CreatorsHost.Read<CreatorPageDto>(response);
    }

    private async Task<PoiCreatorsDto> PoiBlockAsync(Guid poi)
    {
        using var traveler = _host.Traveler();
        return await CreatorsHost.Read<PoiCreatorsDto>(await traveler.GetAsync($"/api/creators/v1/pois/{poi}/contents", Ct));
    }

    private async Task<bool> ListedAsync(Guid creatorId, string query)
    {
        using var traveler = _host.Traveler();
        var list = await CreatorsHost.Read<CreatorListDto>(await traveler.GetAsync($"/api/creators/v1/creators?{query}&limit=50", Ct));
        return list.Items.Any(item => item.Id == creatorId);
    }

    [Fact]
    public async Task A_creator_page_shows_the_places_with_their_tips_and_the_contents_as_links_out()
    {
        var creator = await _host.FounderAsync(publish: true, specialties: ["nature", "history.maritime"]);
        var fort = await _host.PoiAsync("Fort Saint-Jean");
        var calanque = await _host.PoiAsync("Calanque de Sormiou", city: "Marseille");
        var video = await _host.ContentAsync(creator.Id, duration: 900, commercial: true, chapters: [new ChapterDto(135, "Le fort"), new ChapterDto(400, "La calanque")]);
        await _host.LinkAsync(creator.Id, fort, video.Id, 135);
        await _host.LinkAsync(creator.Id, calanque, video.Id, 400);
        using var admin = _host.Admin();
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/tips/{fort}", new SetTipRequest("Viens au coucher du soleil, côté ouest."), Ct);

        var page = (await PageAsync(creator.Handle.ToUpperInvariant()))!; // the handle is found whatever its case

        page.ShouldSatisfyAllConditions(
            p => p.Handle.ShouldBe(creator.Handle),
            p => p.Specialties.ShouldBe(["nature", "history.maritime"]),
            p => p.PlaceCount.ShouldBe(2),
            p => p.DestinationCount.ShouldBe(1),
            p => p.IsNew.ShouldBeTrue(),
            p => p.FollowerCount.ShouldBeNull(),
            p => p.IsFollowing.ShouldBeFalse());
        var firstPlace = page.Places.Single(place => place.PoiId == fort);
        firstPlace.Tip.ShouldBe("Viens au coucher du soleil, côté ouest.");
        var content = firstPlace.Contents.Single();
        content.Url.ShouldBe($"{video.Permalink}&t=135s"); // the link out, timestamped for the chapter; never an embed
        (content.IsCommercial, content.StartSeconds, content.Platform).ShouldBe((true, 135, "youtube"));
        page.Places.Single(place => place.PoiId == calanque).Contents.Single().Url.ShouldEndWith("&t=400s");
        page.RecentContents.Single().Url.ShouldBe(video.Permalink); // the content as a whole has no chapter
    }

    [Fact]
    public async Task Only_what_is_validated_published_and_online_is_exposed_by_every_read()
    {
        var creator = await _host.FounderAsync(publish: true, specialties: "culture");
        var poi = await _host.PoiAsync("Vieille Charité");
        var content = await _host.ContentAsync(creator.Id);
        var link = await _host.LinkAsync(creator.Id, poi, content.Id);
        using var admin = _host.Admin();
        var filter = "specialty=culture&destination=marseille";

        async Task AssertVisibleAsync(bool visible, string because)
        {
            (await PageAsync(creator.Handle) is { Places.Count: > 0 }).ShouldBe(visible, because + " (page)");
            (await PoiBlockAsync(poi)).Items.Any(item => item.Creator.Id == creator.Id).ShouldBe(visible, because + " (place block)");
            (await ListedAsync(creator.Id, filter)).ShouldBe(visible, because + " (list)");
        }

        await AssertVisibleAsync(true, "validated, published, online");

        // A link that is only proposed, then rejected, is never shown.
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/place-links/{link.Id}", new SetPlaceLinkStatusRequest("proposed"), Ct);
        await AssertVisibleAsync(false, "proposed link");
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/place-links/{link.Id}", new SetPlaceLinkStatusRequest("rejected"), Ct);
        await AssertVisibleAsync(false, "rejected link");
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/place-links/{link.Id}", new SetPlaceLinkStatusRequest("validated"), Ct);
        await AssertVisibleAsync(true, "validated again");

        // The place withdrawn from the catalog.
        await _host.PoiAsync("Vieille Charité", published: false, version: 2, id: poi);
        await AssertVisibleAsync(false, "place unpublished");
        await _host.PoiAsync("Vieille Charité", published: true, version: 3, id: poi);
        await AssertVisibleAsync(true, "place published again");

        // The content hidden, then online again.
        var hidden = new UpdateContentRequest(content.Title, null, content.CoverPath, content.DurationSeconds, false, null, "hidden");
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/contents/{content.Id}", hidden, Ct);
        await AssertVisibleAsync(false, "content hidden");
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/contents/{content.Id}", hidden with { Status = "imported" }, Ct);
        await AssertVisibleAsync(true, "content online again");

        // The creator withdrawn.
        await admin.PostAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/unpublish", new ReasonRequest("test"), Ct);
        await AssertVisibleAsync(false, "creator unpublished");
    }

    [Fact]
    public async Task Hiding_or_removing_a_content_withdraws_its_places_and_bringing_it_back_restores_them()
    {
        var creator = await _host.FounderAsync(publish: true);
        var poi = await _host.PoiAsync("Palais Longchamp");
        var content = await _host.ContentAsync(creator.Id);
        await _host.LinkAsync(creator.Id, poi, content.Id);
        using var admin = _host.Admin();
        var id = creator.Id;
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(1); // validated

        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{id}/contents/{content.Id}", new UpdateContentRequest(content.Title, null, null, null, false, null, "hidden"), Ct);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(2);
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{id}/contents/{content.Id}", new UpdateContentRequest(content.Title, null, null, null, false, null, "imported"), Ct);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(3);

        (await admin.DeleteAsync($"/api/creators/v1/admin/creators/{id}/contents/{content.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(4);
        (await PoiBlockAsync(poi)).Total.ShouldBe(0);
        (await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{id}/contents/{content.Id}", new UpdateContentRequest(content.Title, null, null, null, false, null, "imported"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // a removed content stays removed
    }

    [Fact]
    public async Task The_block_of_a_place_lists_each_creator_once_with_tip_and_advertising_flag_and_counts_them_all()
    {
        var poi = await _host.PoiAsync("MUCEM");
        using var admin = _host.Admin();
        var first = await _host.FounderAsync(publish: true);
        var second = await _host.FounderAsync(publish: true);
        var third = await _host.FounderAsync(publish: true);
        foreach (var creator in new[] { first, second, third })
        {
            var video = await _host.ContentAsync(creator.Id, commercial: creator.Id == second.Id);
            await _host.LinkAsync(creator.Id, poi, video.Id);
            await _host.LinkAsync(creator.Id, poi, video.Id, 30); // a second link of the same creator to the same place
        }

        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{first.Id}/tips/{poi}", new SetTipRequest("Y aller le mardi."), Ct);
        using var traveler = _host.Traveler();

        var block = await CreatorsHost.Read<PoiCreatorsDto>(await traveler.GetAsync($"/api/creators/v1/pois/{poi}/contents?limit=2", Ct));

        block.Total.ShouldBe(3);
        block.Items.Count.ShouldBe(2);
        block.Items.Select(item => item.Creator.Id).Distinct().Count().ShouldBe(2);
        var all = await CreatorsHost.Read<PoiCreatorsDto>(await traveler.GetAsync($"/api/creators/v1/pois/{poi}/contents", Ct));
        all.Items.Single(item => item.Creator.Id == first.Id).Tip.ShouldBe("Y aller le mardi.");
        all.Items.Single(item => item.Creator.Id == second.Id).Content!.IsCommercial.ShouldBeTrue(); // the "Publicité" label travels with the content
        all.Items.Single(item => item.Creator.Id == third.Id).Content!.IsCommercial.ShouldBeFalse();
    }

    [Fact]
    public async Task The_follower_count_is_shown_from_twenty()
    {
        var creator = await _host.FounderAsync(publish: true);
        foreach (var _ in Enumerable.Range(0, 19))
        {
            await _host.Execute($"insert into creators.follow (traveler_id, creator_id, followed_at) values ('{Guid.NewGuid()}', '{creator.Id}', now())");
        }

        var before = (await PageAsync(creator.Handle))!;
        (before.IsNew, before.FollowerCount).ShouldBe((true, null));

        await _host.Execute($"insert into creators.follow (traveler_id, creator_id, followed_at) values ('{Guid.NewGuid()}', '{creator.Id}', now())");

        var after = (await PageAsync(creator.Handle))!;
        (after.IsNew, after.FollowerCount).ShouldBe((false, 20));
    }

    [Fact]
    public async Task The_list_sorts_by_validated_places_filters_by_specialty_and_destination_and_pages()
    {
        var specialty = "outdoors.cycling";
        var destination = CreatorsHost.Unique("dest");
        var lots = await _host.FounderAsync(publish: true, specialties: specialty);
        var some = await _host.FounderAsync(publish: true, specialties: specialty);
        var none = await _host.FounderAsync(publish: true, specialties: specialty);
        var other = await _host.FounderAsync(publish: true, specialties: "religion.churches");
        foreach (var name in new[] { "A", "B", "C" })
        {
            await _host.LinkAsync(lots.Id, await _host.PoiAsync($"{name} {destination}", destination: destination));
        }

        await _host.LinkAsync(some.Id, await _host.PoiAsync($"D {destination}", destination: destination));
        await _host.LinkAsync(other.Id, await _host.PoiAsync($"E {destination}", destination: destination));
        using var traveler = _host.Traveler();

        var inDestination = await CreatorsHost.Read<CreatorListDto>(await traveler.GetAsync($"/api/creators/v1/creators?destination={destination}&specialty={specialty}", Ct));
        inDestination.Items.Select(item => (item.Id, item.PlaceCount)).ShouldBe([(lots.Id, 3), (some.Id, 1)]); // none and other are not in this destination for this specialty
        inDestination.NextCursor.ShouldBeNull();

        var bySpecialty = await CreatorsHost.Read<CreatorListDto>(await traveler.GetAsync($"/api/creators/v1/creators?specialty={specialty}&limit=2", Ct));
        bySpecialty.Items.Select(item => item.Id).Take(2).ShouldBe([lots.Id, some.Id]);
        bySpecialty.NextCursor.ShouldBe("2");
        var next = await CreatorsHost.Read<CreatorListDto>(await traveler.GetAsync($"/api/creators/v1/creators?specialty={specialty}&limit=2&cursor=2", Ct));
        next.Items.Select(item => item.Id).ShouldContain(none.Id);
        (await traveler.GetAsync("/api/creators/v1/creators?cursor=abc", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Nothing_in_the_public_answers_identifies_a_follower_or_a_position()
    {
        var creator = await _host.FounderAsync(publish: true);
        using var traveler = _host.Traveler();
        await traveler.PutAsync($"/api/creators/v1/me/follows/{creator.Id}", null, Ct);

        var json = await (await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct)).Content.ReadAsStringAsync(Ct);

        json.ShouldNotContain("travelerId", Case.Insensitive);
        json.ShouldNotContain("latitude", Case.Insensitive);
        json.ShouldNotContain("lng", Case.Insensitive);
    }
}
