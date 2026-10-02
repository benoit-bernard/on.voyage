using System.Net;
using System.Net.Http.Json;
using OnVoyage.Creators.Contracts;
using OnVoyage.TestInfrastructure;

namespace Creators.IntegrationTests;

/// <summary>T-1206: the creator's self-service space. The creator is always the account of the token; the role gates everything but sign-up.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class StudioTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Studio = "/api/creators/v1/studio";
    private const string Terms = "2026-10";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(Guid Account, StudioRegistrationDto Registration)> SignUpAsync(string? handle = null)
    {
        var account = Guid.NewGuid();
        using var client = _host.Account(account);
        var registration = await CreatorsHost.Read<StudioRegistrationDto>(await client.PostAsJsonAsync($"{Studio}/signup", new StudioSignupRequest(handle ?? CreatorsHost.Unique(), "Marie", Terms), Ct));
        return (account, registration);
    }

    [Fact]
    public async Task Studio_routes_answer_403_without_the_creator_role_and_401_without_a_session()
    {
        using var anonymous = _host.Factory.CreateClient();
        using var traveler = _host.Traveler();
        using var account = _host.Account();

        (await anonymous.GetAsync($"{Studio}/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await traveler.GetAsync($"{Studio}/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await account.GetAsync($"{Studio}/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await account.PutAsJsonAsync($"{Studio}/tips/{Guid.NewGuid()}", new SetTipRequest("Au coucher du soleil."), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await account.PostAsync($"{Studio}/publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // An anonymous session cannot even sign up: the creator needs a verified e-mail.
        (await traveler.PostAsJsonAsync($"{Studio}/signup", new StudioSignupRequest(CreatorsHost.Unique(), "Marie", Terms), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.GetAsync($"{Studio}/registration", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_account_that_is_not_yet_a_creator_is_told_so_and_which_terms_to_accept()
    {
        using var client = _host.Account();

        var registration = await CreatorsHost.Read<StudioRegistrationDto>(await client.GetAsync($"{Studio}/registration", Ct));

        registration.ShouldBe(new StudioRegistrationDto(false, Terms, null, null, null, false));
    }

    [Fact]
    public async Task Signing_up_creates_a_draft_records_the_terms_and_asks_platform_for_the_creator_role()
    {
        var (account, registration) = await SignUpAsync();

        registration.Registered.ShouldBeTrue();
        registration.Status.ShouldBe("draft");
        registration.TermsAccepted.ShouldBeTrue();
        (await _host.Queued("platform", "CreatorTermsAcceptedV1", account)).ShouldBe(1);
        (await _host.Scalar<string>($"select terms_version from creators.creator where account_id = '{account}'")).ShouldBe(Terms);

        // The role comes with the next token: Studio then reads and publishes its own sheet.
        using var creator = _host.Account(account, "creator");
        var profile = await CreatorsHost.Read<StudioProfileDto>(await creator.GetAsync($"{Studio}/profile", Ct));
        (profile.Id, profile.Status, profile.Founding, profile.TermsVersion).ShouldBe((registration.CreatorId!.Value, "draft", false, Terms));
        profile.PublishBlock!.Code.ShouldBe("specialty_required");
    }

    [Fact]
    public async Task Signing_up_without_the_current_terms_is_refused_and_creates_nothing()
    {
        using var client = _host.Account();
        var handle = CreatorsHost.Unique();

        var response = await client.PostAsJsonAsync($"{Studio}/signup", new StudioSignupRequest(handle, "Marie", "2001-01"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("terms_required");
        (await _host.Scalar<long>($"select count(*) from creators.creator where handle = '{handle}'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_taken_handle_is_refused_whatever_its_case_with_a_suggestion_and_signing_up_twice_is_harmless()
    {
        var handle = CreatorsHost.Unique();
        var (account, first) = await SignUpAsync(handle);
        using var other = _host.Account();

        var refused = await other.PostAsJsonAsync($"{Studio}/signup", new StudioSignupRequest(handle.ToUpperInvariant(), "Autre", Terms), Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Suggestion");
        using var again = _host.Account(account);
        var repeated = await CreatorsHost.Read<StudioRegistrationDto>(await again.PostAsJsonAsync($"{Studio}/signup", new StudioSignupRequest("autre_handle", "Marie", Terms), Ct));
        repeated.CreatorId.ShouldBe(first.CreatorId);
        (await _host.Queued("platform", "CreatorTermsAcceptedV1", account)).ShouldBe(1);
    }

    [Fact]
    public async Task A_creator_edits_their_profile_publishes_and_withdraws_their_page_and_the_handle_is_then_locked()
    {
        var (account, registration) = await SignUpAsync();
        using var creator = _host.Account(account, "creator");
        var request = CreatorsHost.Profile(registration.Handle, "Marie Marseille", "history", "nature");

        var saved = await CreatorsHost.Read<StudioProfileDto>(await creator.PutAsJsonAsync($"{Studio}/profile", request, Ct));
        saved.Specialties.ShouldBe(["history", "nature"]);
        saved.PublishBlock.ShouldBeNull();

        var published = await CreatorsHost.Read<StudioProfileDto>(await creator.PostAsync($"{Studio}/publish", null, Ct));
        published.Status.ShouldBe("published");
        (await _host.Queued("discovery", "CreatorPublishedV1", registration.CreatorId!.Value)).ShouldBe(1);
        using var traveler = _host.Traveler();
        (await traveler.GetAsync($"/api/creators/v1/creators/{registration.Handle}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var renamed = await creator.PutAsJsonAsync($"{Studio}/profile", request with { Handle = CreatorsHost.Unique() }, Ct);
        renamed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await renamed.Content.ReadAsStringAsync(Ct)).ShouldContain("handle_locked");

        var withdrawn = await CreatorsHost.Read<StudioProfileDto>(await creator.PostAsync($"{Studio}/unpublish", null, Ct));
        withdrawn.Status.ShouldBe("draft");
        (await traveler.GetAsync($"/api/creators/v1/creators/{registration.Handle}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _host.Queued("discovery", "CreatorUnpublishedV1", registration.CreatorId.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task A_creator_sees_no_follower_count_below_twenty_and_never_the_followers()
    {
        var (account, registration) = await SignUpAsync();
        using var creator = _host.Account(account, "creator");
        await creator.PutAsJsonAsync($"{Studio}/profile", CreatorsHost.Profile(registration.Handle), Ct);
        await creator.PostAsync($"{Studio}/publish", null, Ct);
        using var fan = _host.Traveler();
        await fan.PutAsync($"/api/creators/v1/me/follows/{registration.CreatorId}", null, Ct);

        var profile = await CreatorsHost.Read<StudioProfileDto>(await creator.GetAsync($"{Studio}/profile", Ct));

        profile.FollowerCount.ShouldBeNull();
        profile.IsNew.ShouldBeTrue();
        (await creator.GetStringAsync($"{Studio}/profile", Ct)).ShouldNotContain("travelerId", Case.Insensitive);
    }

    [Fact]
    public async Task A_creator_manages_their_contents_tips_and_places_and_cannot_touch_those_of_another()
    {
        var (account, registration) = await SignUpAsync();
        var (otherAccount, otherRegistration) = await SignUpAsync();
        var poi = await _host.PoiAsync("Fort Saint-Jean");
        using var creator = _host.Account(account, "creator");
        using var other = _host.Account(otherAccount, "creator");

        var content = await CreatorsHost.Read<AdminContentDto>(
            await creator.PostAsJsonAsync($"{Studio}/contents", new AddContentRequest($"https://www.youtube.com/watch?v={CreatorsHost.Unique("v")[..11].PadRight(11, 'z')}", "Marseille en 48 h", null, null, null, 600, null, false, [new ChapterDto(135, "Fort Saint-Jean")]), Ct),
            HttpStatusCode.Created);
        var link = await CreatorsHost.Read<AdminPlaceLinkDto>(await creator.PostAsJsonAsync($"{Studio}/place-links", new AddPlaceLinkRequest(poi, content.Id, 135, null), Ct));
        var tip = await CreatorsHost.Read<AdminTipDto>(await creator.PutAsJsonAsync($"{Studio}/tips/{poi}", new SetTipRequest("Viens au coucher du soleil."), Ct));
        link.Status.ShouldBe("validated");
        tip.Text.ShouldBe("Viens au coucher du soleil.");

        var profile = await CreatorsHost.Read<StudioProfileDto>(await creator.GetAsync($"{Studio}/profile", Ct));
        profile.Contents.Select(item => item.Id).ShouldBe([content.Id]);
        profile.Tips.Count.ShouldBe(1);

        // Another creator's identifiers are simply unknown to this one.
        (await other.DeleteAsync($"{Studio}/contents/{content.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PutAsJsonAsync($"{Studio}/place-links/{link.Id}", new SetPlaceLinkStatusRequest("rejected"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _host.Detail(registration.CreatorId!.Value)).Contents.Count.ShouldBe(1);
        (await _host.Detail(otherRegistration.CreatorId!.Value)).PlaceLinks.ShouldBeEmpty();

        (await creator.PutAsJsonAsync($"{Studio}/tips/{poi}", new SetTipRequest(new string('x', 281)), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await creator.DeleteAsync($"{Studio}/tips/{poi}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await creator.DeleteAsync($"{Studio}/contents/{content.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_suspended_creator_cannot_write_nor_lift_the_suspension()
    {
        var (account, registration) = await SignUpAsync();
        using var creator = _host.Account(account, "creator");
        await creator.PutAsJsonAsync($"{Studio}/profile", CreatorsHost.Profile(registration.Handle), Ct);
        using var admin = _host.Admin();
        await admin.PostAsJsonAsync($"/api/creators/v1/admin/creators/{registration.CreatorId}/suspend", new ReasonRequest("test"), Ct);

        var publish = await creator.PostAsync($"{Studio}/publish", null, Ct);
        var edit = await creator.PutAsJsonAsync($"{Studio}/profile", CreatorsHost.Profile(registration.Handle, "Autre nom"), Ct);

        publish.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        edit.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _host.Detail(registration.CreatorId!.Value)).Status.ShouldBe("suspended");
        (await creator.GetAsync($"{Studio}/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_founding_creator_who_signs_in_with_the_linked_account_is_found_and_accepting_the_terms_keeps_the_founder_flag()
    {
        var account = Guid.NewGuid();
        var founder = await _host.FounderAsync(account: account);
        using var client = _host.Account(account);

        var registration = await CreatorsHost.Read<StudioRegistrationDto>(await client.GetAsync($"{Studio}/registration", Ct));
        registration.CreatorId.ShouldBe(founder.Id);
        registration.TermsAccepted.ShouldBeTrue(); // the signed consent stands for the terms (F-26)

        var accepted = await CreatorsHost.Read<StudioRegistrationDto>(await client.PostAsJsonAsync($"{Studio}/terms", new AcceptTermsRequest(Terms), Ct));
        accepted.TermsAccepted.ShouldBeTrue();
        var detail = await _host.Detail(founder.Id);
        (detail.Founding, detail.TermsVersion, detail.TermsDocumentRef).ShouldBe((true, Terms, null));
    }
}
