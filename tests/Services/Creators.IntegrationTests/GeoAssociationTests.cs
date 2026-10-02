using System.Net;
using System.Net.Http.Json;
using OnVoyage.Creators.Contracts;
using OnVoyage.TestInfrastructure;

namespace Creators.IntegrationTests;

/// <summary>
/// T-1209 on PostgreSQL with the offline reader: contents are analysed in the background, the places they mention become proposals (never published),
/// and only the creator (or an administrator) turns a proposal into a validated association.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GeoAssociationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Studio = "/api/creators/v1/studio";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Creator(Guid Account, Guid Id, HttpClient Client, string Handle);

    private async Task<Creator> NewCreatorAsync(bool publish = false)
    {
        var account = Guid.NewGuid();
        using var join = _host.Account(account);
        var handle = CreatorsHost.Unique();
        var registration = await CreatorsHost.Read<StudioRegistrationDto>(await join.PostAsJsonAsync($"{Studio}/signup", new StudioSignupRequest(handle, "Marie", "2026-10"), Ct));
        var client = _host.Account(account, "creator");
        await client.PutAsJsonAsync($"{Studio}/profile", CreatorsHost.Profile(handle, "Marie", "history"), Ct);
        if (publish)
        {
            await client.PostAsync($"{Studio}/publish", null, Ct);
        }

        return new Creator(account, registration.CreatorId!.Value, client, handle);
    }

    /// <summary>A name no other test uses: one capitalised word of letters only, which is what the offline reader picks out.</summary>
    private static string Unique(string prefix) => prefix + new string([.. Enumerable.Range(0, 7).Select(_ => (char)('a' + Random.Shared.Next(26)))]);

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var value = await read();
        for (var attempt = 0; attempt < 100 && !done(value); attempt++)
        {
            await Task.Delay(100, Ct);
            value = await read();
        }

        return value;
    }

    private async Task<AdminContentDto> AddContentAsync(Creator creator, string caption, params ChapterDto[] chapters)
    {
        var url = $"https://www.youtube.com/watch?v={CreatorsHost.Unique("v")[..11].PadRight(11, 'q')}";
        var content = await CreatorsHost.Read<AdminContentDto>(
            await creator.Client.PostAsJsonAsync($"{Studio}/contents", new AddContentRequest(url, "Mon voyage", caption, null, null, 900, null, false, chapters), Ct), HttpStatusCode.Created);
        await EventuallyAsync(() => _host.Scalar<bool>($"select geotagged_at is not null from creators.content_item where id = '{content.Id}'"), done => done);
        return content;
    }

    private static async Task<PlaceProposalsDto> ProposalsAsync(Creator creator) =>
        await CreatorsHost.Read<PlaceProposalsDto>(await creator.Client.GetAsync($"{Studio}/place-links?status=proposed", Ct));

    [Fact]
    public async Task A_new_content_is_analysed_in_the_background_and_its_places_become_proposals_by_destination_and_nothing_is_published()
    {
        var creator = await NewCreatorAsync(publish: true);
        var gordes = Unique("Gord");
        var cassis = Unique("Cass");
        var gordesPoi = await _host.PoiAsync(gordes, destination: "provence", city: "Luberon");
        var cassisPoi = await _host.PoiAsync(cassis, destination: "provence", city: "Cassis");
        var vieuxPort = Unique("Vieu");
        var vieuxPoi = await _host.PoiAsync(vieuxPort, destination: "marseille");

        var content = await AddContentAsync(creator, $"Dimanche à {gordes} puis {cassis}, et le soir {vieuxPort} #voyage");

        var proposals = await ProposalsAsync(creator);
        proposals.Total.ShouldBe(3);
        proposals.Groups.Select(group => (group.Destination, group.Items.Count)).ShouldBe([("provence", 2), ("marseille", 1)]);
        var all = proposals.Groups.SelectMany(group => group.Items).ToList();
        all.Select(item => item.PoiId).ShouldBe([gordesPoi, cassisPoi, vieuxPoi], ignoreOrder: true);
        all.ShouldAllBe(item => item.Confidence >= 0.9 && item.ContentId == content.Id && item.Evidence != null && item.Signals.Contains("text"));
        proposals.ReadyCount.ShouldBe(3);
        proposals.BulkThreshold.ShouldBe(0.9);
        proposals.Pending.ShouldBe(0);

        // Not published: neither on the creator's public page, nor announced to Discovery, nor a validated row.
        using var traveler = _host.Traveler();
        var page = await CreatorsHost.Read<CreatorPageDto>(await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct));
        page.Places.ShouldBeEmpty();
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", gordesPoi)).ShouldBe(0);
        (await _host.Scalar<long>($"select count(*) from creators.place_link where creator_id = '{creator.Id}' and status <> 'proposed'")).ShouldBe(0);
        (await _host.Detail(creator.Id)).PlaceLinks.ShouldAllBe(link => link.Status == "proposed");
    }

    [Fact]
    public async Task The_chapters_of_a_video_each_give_a_proposal_with_a_link_to_the_content_at_that_time()
    {
        var creator = await NewCreatorAsync();
        var first = Unique("Roux");
        var second = Unique("Lour");
        var firstPoi = await _host.PoiAsync(first, destination: "provence");
        var secondPoi = await _host.PoiAsync(second, destination: "provence");

        var content = await AddContentAsync(creator, "Mes étapes", new ChapterDto(135, first), new ChapterDto(340, second), new ChapterDto(500, "Conclusion"));

        var items = (await ProposalsAsync(creator)).Groups.SelectMany(group => group.Items).OrderBy(item => item.StartSeconds).ToList();
        items.Select(item => (item.PoiId, item.StartSeconds)).ShouldBe([(firstPoi, (int?)135), (secondPoi, 340)]);
        items[0].Signals.ShouldContain("chapter");
        items[0].ContentUrl.ShouldEndWith("&t=135s");
        items[1].ContentUrl.ShouldEndWith("&t=340s");
        items.ShouldAllBe(item => item.Confidence >= 0.9 && item.ContentTitle == content.Title);
    }

    [Fact]
    public async Task Tout_valider_publishes_the_sure_proposals_only_and_the_rest_waits_for_a_one_by_one_review()
    {
        var creator = await NewCreatorAsync(publish: true);
        var sure = Unique("Arle");
        var partialName = Unique("Sormi");
        var sureNothing = await _host.PoiAsync(sure, destination: "provence");
        var partialPoi = await _host.PoiAsync($"Calanque de {partialName}", destination: "marseille");
        await AddContentAsync(creator, $"Étape à {sure} et randonnée vers {partialName}");
        var before = await ProposalsAsync(creator);
        before.Total.ShouldBe(2);
        before.ReadyCount.ShouldBe(1);

        var asked = await creator.Client.PostAsJsonAsync($"{Studio}/place-links/validate", new ReviewPlaceLinksRequest(null, 0.1), Ct); // asking for less than the threshold changes nothing
        var result = await CreatorsHost.Read<ReviewResultDto>(asked);

        result.Validated.ShouldBe(1);
        var after = await ProposalsAsync(creator);
        after.Groups.SelectMany(group => group.Items).Select(item => item.PoiId).ShouldBe([partialPoi]);
        after.Groups.SelectMany(group => group.Items).Single().Confidence.ShouldBeLessThan(0.9);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", sureNothing)).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", partialPoi)).ShouldBe(0);
        using var traveler = _host.Traveler();
        (await CreatorsHost.Read<CreatorPageDto>(await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct))).Places.Select(place => place.PoiId).ShouldBe([sureNothing]);
    }

    [Fact]
    public async Task A_proposal_is_validated_rejected_for_good_or_corrected_by_the_creator()
    {
        var creator = await NewCreatorAsync(publish: true);
        var kept = Unique("Kept");
        var dropped = Unique("Drop");
        var wrong = Unique("Wron");
        var keptPoi = await _host.PoiAsync(kept, destination: "provence");
        var droppedPoi = await _host.PoiAsync(dropped, destination: "provence");
        var wrongPoi = await _host.PoiAsync(wrong, destination: "provence");
        var rightPoi = await _host.PoiAsync(Unique("Righ"), destination: "provence");
        var content = await AddContentAsync(creator, $"Étapes : {kept}, {dropped}, {wrong}");
        var items = (await ProposalsAsync(creator)).Groups.SelectMany(group => group.Items).ToDictionary(item => item.PoiId);
        items.Count.ShouldBe(3);

        var validated = await CreatorsHost.Read<ReviewResultDto>(await creator.Client.PostAsJsonAsync($"{Studio}/place-links/validate", new ReviewPlaceLinksRequest([items[keptPoi].LinkId], null), Ct));
        var rejected = await CreatorsHost.Read<ReviewResultDto>(await creator.Client.PostAsJsonAsync($"{Studio}/place-links/reject", new ReviewPlaceLinksRequest([items[droppedPoi].LinkId], null), Ct));
        var corrected = await CreatorsHost.Read<AdminPlaceLinkDto>(await creator.Client.PostAsJsonAsync($"{Studio}/place-links/{items[wrongPoi].LinkId}/correct", new CorrectPlaceLinkRequest(rightPoi), Ct));

        (validated.Validated, rejected.Rejected).ShouldBe((1, 1));
        (corrected.PoiId, corrected.Status, corrected.ContentId).ShouldBe((rightPoi, "validated", content.Id));
        (await ProposalsAsync(creator)).Total.ShouldBe(0);
        var links = (await _host.Detail(creator.Id)).PlaceLinks.ToDictionary(link => link.PoiId, link => link.Status);
        (links[keptPoi], links[droppedPoi], links[wrongPoi], links[rightPoi]).ShouldBe(("validated", "rejected", "rejected", "validated"));
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", keptPoi)).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", rightPoi)).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", droppedPoi)).ShouldBe(0);

        // A rejected proposal is never proposed again, even when the content is analysed once more.
        await creator.Client.PostAsync($"{Studio}/place-links/analyze?force=true", null, Ct);
        await Task.Delay(1500, Ct);
        (await ProposalsAsync(creator)).Total.ShouldBe(0);
        (await _host.Detail(creator.Id)).PlaceLinks.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_place_unknown_to_the_catalog_is_suggested_once_to_the_editorial_team()
    {
        var creator = await NewCreatorAsync();
        var known = Unique("Know");
        var unknown = Unique("Inco");
        await _host.PoiAsync(known, destination: "marseille");

        var content = await AddContentAsync(creator, $"Départ de {known}, direction {unknown} !");
        await creator.Client.PostAsync($"{Studio}/place-links/analyze?force=true", null, Ct);
        await Task.Delay(1500, Ct);

        (await _host.Queued("factory", "PlaceSuggestedV1", unknown)).ShouldBe(1);
        (await _host.Queued("factory", "PlaceSuggestedV1", content.Id)).ShouldBe(1);
        (await _host.Scalar<long>($"select count(*) from creators.unmatched_mention where content_id = '{content.Id}'")).ShouldBe(1);
        (await ProposalsAsync(creator)).Total.ShouldBe(1); // the known place is a proposal, the unknown one a suggestion: one suggestion for the content, as counted above
    }

    [Fact]
    public async Task A_creator_only_sees_and_decides_their_own_proposals_and_a_suspended_one_cannot_decide()
    {
        var marie = await NewCreatorAsync();
        var other = await NewCreatorAsync();
        var place = Unique("Priv");
        var poi = await _host.PoiAsync(place, destination: "marseille");
        await AddContentAsync(marie, $"Vue sur {place}");
        var mine = (await ProposalsAsync(marie)).Groups.SelectMany(group => group.Items).Single();

        (await ProposalsAsync(other)).Total.ShouldBe(0);
        var stolen = await CreatorsHost.Read<ReviewResultDto>(await other.Client.PostAsJsonAsync($"{Studio}/place-links/validate", new ReviewPlaceLinksRequest([mine.LinkId], null), Ct));
        (await other.Client.PostAsJsonAsync($"{Studio}/place-links/{mine.LinkId}/correct", new CorrectPlaceLinkRequest(poi), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        stolen.Validated.ShouldBe(0);
        (await ProposalsAsync(marie)).Total.ShouldBe(1);

        using var admin = _host.Admin();
        await admin.PostAsJsonAsync($"/api/creators/v1/admin/creators/{marie.Id}/suspend", new ReasonRequest("test"), Ct);
        (await marie.Client.PostAsJsonAsync($"{Studio}/place-links/validate", new ReviewPlaceLinksRequest([mine.LinkId], null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_administrator_can_validate_a_proposal_too()
    {
        var creator = await NewCreatorAsync(publish: true);
        var place = Unique("Admi");
        var poi = await _host.PoiAsync(place, destination: "marseille");
        await AddContentAsync(creator, $"Visite de {place}");
        var proposal = (await ProposalsAsync(creator)).Groups.SelectMany(group => group.Items).Single();

        using var admin = _host.Admin();
        var link = await CreatorsHost.Read<AdminPlaceLinkDto>(await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/place-links/{proposal.LinkId}", new SetPlaceLinkStatusRequest("validated"), Ct));

        link.Status.ShouldBe("validated");
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(1);
    }

    [Fact]
    public async Task Contents_not_analysed_yet_are_counted_and_the_analysis_can_be_asked_for()
    {
        var creator = await NewCreatorAsync();
        var place = Unique("Late");
        await _host.PoiAsync(place, destination: "marseille");
        var content = await AddContentAsync(creator, "Rien pour l'instant");
        await _host.Execute($"update creators.content_item set geotagged_at = null, caption_excerpt = 'Balade à {place}' where id = '{content.Id}'");
        (await ProposalsAsync(creator)).Pending.ShouldBe(1);

        var asked = await creator.Client.PostAsync($"{Studio}/place-links/analyze", null, Ct);

        (await CreatorsHost.Read<AnalysisRequestedDto>(asked, HttpStatusCode.Accepted)).Contents.ShouldBe(1);
        var proposals = await EventuallyAsync(() => ProposalsAsync(creator), found => found.Total == 1 && found.Pending == 0);
        proposals.Total.ShouldBe(1);
    }

    [Fact]
    public async Task The_review_routes_are_for_creators_only_and_the_export_lists_the_suggested_places()
    {
        using var account = _host.Account();
        (await account.GetAsync($"{Studio}/place-links", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await account.PostAsJsonAsync($"{Studio}/place-links/validate", new ReviewPlaceLinksRequest(null, 0.95), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var creator = await NewCreatorAsync();
        var unknown = Unique("Expo");
        await AddContentAsync(creator, $"Direction {unknown} demain");
        var export = await _host.Bus.InvokeAsync<OnVoyage.Platform.Contracts.TravelerExportPartReadyV1>(new OnVoyage.Platform.Contracts.TravelerExportRequestedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), creator.Account), Ct);
        var json = await File.ReadAllTextAsync(Path.Combine(_host.Exports, export.Path), Ct);
        json.ShouldContain("placeSuggestions");
        json.ShouldContain(unknown);

        (await creator.Client.GetAsync($"{Studio}/place-links?status=validated", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
