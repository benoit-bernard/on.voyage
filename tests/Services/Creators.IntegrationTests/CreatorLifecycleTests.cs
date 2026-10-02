using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Creators.Contracts;
using OnVoyage.TestInfrastructure;

namespace Creators.IntegrationTests;

/// <summary>The founding creators of the admin: consent, publication rule of F-26, handle, suspension, claim, and the events and audit that follow.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CreatorLifecycleTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Path(Guid id, string suffix = "") => $"/api/creators/v1/admin/creators/{id}{suffix}";

    [Fact]
    public async Task A_creator_without_terms_nor_founder_consent_is_not_publishable_and_is_published_once_the_consent_is_recorded()
    {
        using var admin = _host.Admin();
        var created = await _host.FounderAsync(consent: false);
        created.Status.ShouldBe("draft");
        created.PublishBlock!.Code.ShouldBe("terms_required");

        var refused = await admin.PostAsync(Path(created.Id, "/publish"), null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("type").GetString().ShouldBe("https://on.voyage/problems/terms_required");
        problem.RootElement.GetProperty("code").GetString().ShouldBe("terms_required");
        (await _host.Detail(created.Id)).Status.ShouldBe("draft");
        (await _host.Queued("discovery", "CreatorPublishedV1", created.Id)).ShouldBe(0);

        var consenting = await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PutAsJsonAsync(Path(created.Id, "/consent"), new FounderConsentRequest("H-009/marie-2026-09-30.pdf", null), Ct));
        (consenting.TermsVersion, consenting.TermsDocumentRef, consenting.Founding, consenting.PublishBlock).ShouldBe(("fondateur", "H-009/marie-2026-09-30.pdf", true, null));

        var published = await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PostAsync(Path(created.Id, "/publish"), null, Ct));
        published.Status.ShouldBe("published");
        (await _host.Queued("discovery", "CreatorPublishedV1", created.Id)).ShouldBe(1);

        // Platform's journal receives one entry per write, in the same transaction as the write.
        (await _host.Queued("platform", "AdminActionRecordedV1", created.Id)).ShouldBe(3); // create, consent, publish
        (await _host.Queued("platform", "AdminActionRecordedV1", "creator.publish")).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_consent_needs_its_document_reference()
    {
        using var admin = _host.Admin();
        var created = await _host.FounderAsync(consent: false);

        var response = await admin.PutAsJsonAsync(Path(created.Id, "/consent"), new FounderConsentRequest("  ", null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _host.Detail(created.Id)).TermsVersion.ShouldBeNull();
    }

    [Fact]
    public async Task A_creator_with_no_specialty_is_not_publishable_even_with_consent()
    {
        using var admin = _host.Admin();
        var request = CreatorsHost.Profile() with { Specialties = [] };
        var created = await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PostAsJsonAsync("/api/creators/v1/admin/creators", request, Ct), HttpStatusCode.Created);
        await admin.PutAsJsonAsync(Path(created.Id, "/consent"), new FounderConsentRequest("doc", null), Ct);

        var refused = await admin.PostAsync(Path(created.Id, "/publish"), null, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("specialty_required");
    }

    [Fact]
    public async Task A_taken_handle_is_refused_whatever_its_case_and_a_free_one_is_suggested()
    {
        using var admin = _host.Admin();
        var handle = CreatorsHost.Unique("Mar");
        await _host.FounderAsync(handle);

        var response = await admin.PostAsJsonAsync("/api/creators/v1/admin/creators", CreatorsHost.Profile("@" + handle.ToLowerInvariant()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("code").GetString().ShouldBe("handle_taken");
        var suggestion = problem.RootElement.GetProperty("suggestion").GetString()!;
        suggestion.ShouldBe(handle.ToLowerInvariant() + "2");
        (await admin.PostAsJsonAsync("/api/creators/v1/admin/creators", CreatorsHost.Profile(suggestion), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("with space")]
    [InlineData("tiret-interdit")]
    public async Task An_invalid_handle_is_refused(string handle)
    {
        using var admin = _host.Admin();

        var response = await admin.PostAsJsonAsync("/api/creators/v1/admin/creators", CreatorsHost.Profile(handle), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("invalid_handle");
    }

    [Fact]
    public async Task Unpublishing_and_suspending_withdraw_the_page_and_publish_the_reason()
    {
        using var admin = _host.Admin();
        using var traveler = _host.Traveler();
        var creator = await _host.FounderAsync(publish: true);
        (await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await admin.PostAsJsonAsync(Path(creator.Id, "/unpublish"), new ReasonRequest(" "), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var unpublished = await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PostAsJsonAsync(Path(creator.Id, "/unpublish"), new ReasonRequest("à la demande de Marie"), Ct));

        unpublished.Status.ShouldBe("draft");
        (await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _host.Queued("discovery", "CreatorUnpublishedV1", "à la demande de Marie")).ShouldBe(1);
        (await admin.PostAsJsonAsync(Path(creator.Id, "/unpublish"), new ReasonRequest("encore"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await admin.PostAsync(Path(creator.Id, "/publish"), null, Ct);
        var suspended = await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PostAsJsonAsync(Path(creator.Id, "/suspend"), new ReasonRequest("contenu trompeur signalé"), Ct));
        suspended.Status.ShouldBe("suspended");
        (await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _host.Queued("discovery", "CreatorUnpublishedV1", "contenu trompeur signalé")).ShouldBe(1);
    }

    [Fact]
    public async Task Updating_a_published_profile_announces_it_again_and_cannot_remove_the_last_specialty()
    {
        using var admin = _host.Admin();
        var creator = await _host.FounderAsync(publish: true);
        var profile = CreatorsHost.Profile(creator.Handle, "Marie Gonzalez", "nature", "history.maritime");

        var updated = await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PutAsJsonAsync(Path(creator.Id), profile, Ct));

        updated.DisplayName.ShouldBe("Marie Gonzalez");
        (await _host.Queued("discovery", "CreatorPublishedV1", "Marie Gonzalez")).ShouldBe(1);
        var refused = await admin.PutAsJsonAsync(Path(creator.Id), profile with { Specialties = [] }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_creator_role_is_requested_once_the_terms_are_accepted_and_an_account_is_linked_in_either_order()
    {
        using var admin = _host.Admin();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();

        // Consent first, account after.
        var consentFirst = await _host.FounderAsync(consent: true);
        (await _host.Queued("platform", "CreatorTermsAcceptedV1", consentFirst.Id)).ShouldBe(0);
        await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PutAsJsonAsync(Path(consentFirst.Id, "/account"), new LinkAccountRequest(firstAccount), Ct));
        (await _host.Queued("platform", "CreatorTermsAcceptedV1", firstAccount)).ShouldBe(1);

        // Account first, consent after.
        var accountFirst = await _host.FounderAsync(consent: false, account: secondAccount);
        (await _host.Queued("platform", "CreatorTermsAcceptedV1", secondAccount)).ShouldBe(0);
        await admin.PutAsJsonAsync(Path(accountFirst.Id, "/consent"), new FounderConsentRequest("doc", null), Ct);
        (await _host.Queued("platform", "CreatorTermsAcceptedV1", secondAccount)).ShouldBe(1);
        (await _host.Queued("platform", "CreatorTermsAcceptedV1", "fondateur")).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task An_account_serves_a_single_creator()
    {
        using var admin = _host.Admin();
        var account = Guid.NewGuid();
        await _host.FounderAsync(account: account);
        var other = await _host.FounderAsync(consent: false);

        var response = await admin.PutAsJsonAsync(Path(other.Id, "/account"), new LinkAccountRequest(account), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_handle_can_be_claimed_by_its_true_holder_and_the_impersonator_is_renamed_and_withdrawn()
    {
        using var admin = _host.Admin();
        using var traveler = _host.Traveler();
        var handle = CreatorsHost.Unique("claim");
        var squatter = await _host.FounderAsync(handle, publish: true);
        var holder = await _host.FounderAsync(CreatorsHost.Unique("real"), publish: true);

        var claimed = await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PostAsJsonAsync(Path(holder.Id, "/handle-claim"), new ClaimHandleRequest("@" + handle, "Titulaire vérifié par e-mail"), Ct));

        claimed.Handle.ShouldBe(handle);
        var renamed = await _host.Detail(squatter.Id);
        renamed.Handle.ShouldBe(handle + "2");
        renamed.Status.ShouldBe("draft");
        (await traveler.GetAsync($"/api/creators/v1/creators/{handle}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CreatorsHost.Read<CreatorPageDto>(await traveler.GetAsync($"/api/creators/v1/creators/{handle}", Ct))).Id.ShouldBe(holder.Id);
        (await _host.Queued("discovery", "CreatorUnpublishedV1", squatter.Id)).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorPublishedV1", holder.Id)).ShouldBe(2); // when published, then again under its new handle
        (await _host.Queued("platform", "AdminActionRecordedV1", "creator.claim_handle")).ShouldBeGreaterThanOrEqualTo(1);

        // A reason is required; claiming a free handle simply renames.
        (await admin.PostAsJsonAsync(Path(holder.Id, "/handle-claim"), new ClaimHandleRequest("libre" + Guid.NewGuid().ToString("N")[..8], " "), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var free = CreatorsHost.Unique("libre");
        (await CreatorsHost.Read<AdminCreatorDetailDto>(await admin.PostAsJsonAsync(Path(holder.Id, "/handle-claim"), new ClaimHandleRequest(free, "Changement de nom"), Ct))).Handle.ShouldBe(free);
    }

    [Fact]
    public async Task Only_an_administrator_reaches_the_admin_routes()
    {
        using var anonymous = _host.Factory.CreateClient();
        using var traveler = _host.Traveler();
        using var creatorOnly = _host.Traveler(null, "creator");

        (await anonymous.GetAsync("/api/creators/v1/admin/creators", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await traveler.GetAsync("/api/creators/v1/admin/creators", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await creatorOnly.PostAsJsonAsync("/api/creators/v1/admin/creators", CreatorsHost.Profile(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.GetAsync("/api/creators/v1/admin/moderation", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await anonymous.GetAsync("/api/creators/v1/creators", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_admin_list_filters_by_status_and_text_and_says_why_a_creator_is_not_publishable()
    {
        using var admin = _host.Admin();
        var tag = CreatorsHost.Unique("listed");
        var ready = await _host.FounderAsync(tag + "a", publish: true);
        var blocked = await _host.FounderAsync(tag + "b", consent: false);

        var all = await CreatorsHost.Read<List<AdminCreatorSummaryDto>>(await admin.GetAsync($"/api/creators/v1/admin/creators?search={tag}", Ct));
        var published = await CreatorsHost.Read<List<AdminCreatorSummaryDto>>(await admin.GetAsync($"/api/creators/v1/admin/creators?search={tag}&status=published", Ct));

        all.Select(item => item.Id).ShouldBe([ready.Id, blocked.Id], ignoreOrder: true);
        all.Single(item => item.Id == blocked.Id).PublishBlock!.Code.ShouldBe("terms_required");
        all.Single(item => item.Id == ready.Id).PublishBlock.ShouldBeNull();
        published.Select(item => item.Id).ShouldBe([ready.Id]);
        (await admin.GetAsync("/api/creators/v1/admin/creators?status=nonsense", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
