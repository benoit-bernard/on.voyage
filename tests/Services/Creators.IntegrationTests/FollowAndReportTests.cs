using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Creators.Contracts;
using OnVoyage.TestInfrastructure;

namespace Creators.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class FollowAndReportTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Following_publishes_one_event_repeating_it_none_and_unfollowing_another()
    {
        var creator = await _host.FounderAsync(publish: true);
        var travelerId = Guid.NewGuid();
        using var traveler = _host.Traveler(travelerId);

        var first = await CreatorsHost.Read<FollowStateDto>(await traveler.PutAsync($"/api/creators/v1/me/follows/{creator.Id}", null, Ct));
        await traveler.PutAsync($"/api/creators/v1/me/follows/{creator.Id}", null, Ct);

        first.Following.ShouldBeTrue();
        (await _host.Queued("discovery", "FollowChangedV1", travelerId)).ShouldBe(1);
        (await _host.Scalar<long>($"select count(*) from creators.follow where traveler_id = '{travelerId}'")).ShouldBe(1);

        var list = await CreatorsHost.Read<List<FollowedCreatorDto>>(await traveler.GetAsync("/api/creators/v1/me/follows", Ct));
        list.Select(item => item.Creator.Handle).ShouldBe([creator.Handle]);
        (await CreatorsHost.Read<CreatorPageDto>(await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct))).IsFollowing.ShouldBeTrue();
        using var someoneElse = _host.Traveler();
        (await CreatorsHost.Read<CreatorPageDto>(await someoneElse.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct))).IsFollowing.ShouldBeFalse();
        (await CreatorsHost.Read<List<FollowedCreatorDto>>(await someoneElse.GetAsync("/api/creators/v1/me/follows", Ct))).ShouldBeEmpty();

        (await traveler.DeleteAsync($"/api/creators/v1/me/follows/{creator.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await traveler.DeleteAsync($"/api/creators/v1/me/follows/{creator.Id}", Ct);
        (await _host.Queued("discovery", "FollowChangedV1", travelerId)).ShouldBe(2);
        (await _host.Queued("discovery", "FollowChangedV1", "\"following\":false")).ShouldBeGreaterThanOrEqualTo(1);
        (await CreatorsHost.Read<List<FollowedCreatorDto>>(await traveler.GetAsync("/api/creators/v1/me/follows", Ct))).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_session_can_follow_and_an_unpublished_creator_cannot_be_followed()
    {
        var draft = await _host.FounderAsync(consent: false);
        var published = await _host.FounderAsync(publish: true);
        using var anonymous = _host.Traveler();

        (await anonymous.PutAsync($"/api/creators/v1/me/follows/{draft.Id}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.PutAsync($"/api/creators/v1/me/follows/{Guid.NewGuid()}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.PutAsync($"/api/creators/v1/me/follows/{published.Id}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unpublished_creator_disappears_from_the_followed_list()
    {
        var creator = await _host.FounderAsync(publish: true);
        using var traveler = _host.Traveler();
        using var admin = _host.Admin();
        await traveler.PutAsync($"/api/creators/v1/me/follows/{creator.Id}", null, Ct);

        await admin.PostAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/unpublish", new ReasonRequest("pause"), Ct);

        (await CreatorsHost.Read<List<FollowedCreatorDto>>(await traveler.GetAsync("/api/creators/v1/me/follows", Ct))).ShouldBeEmpty();
    }

    private async Task<ModerationCaseDto> CaseAsync(Guid id)
    {
        using var admin = _host.Admin();
        var queue = await CreatorsHost.Read<List<ModerationCaseDto>>(await admin.GetAsync("/api/creators/v1/admin/moderation?limit=200", Ct));
        return queue.Single(item => item.Id == id);
    }

    [Fact]
    public async Task A_report_enters_the_queue_once_without_telling_who_made_it()
    {
        var creator = await _host.FounderAsync(publish: true);
        using var reporter = _host.Traveler();
        var request = new ReportRequest("creator", creator.Id, "impersonation");

        var receipt = await CreatorsHost.Read<ReportReceiptDto>(await reporter.PostAsJsonAsync("/api/creators/v1/reports", request, Ct), HttpStatusCode.Accepted);
        var again = await CreatorsHost.Read<ReportReceiptDto>(await reporter.PostAsJsonAsync("/api/creators/v1/reports", request, Ct), HttpStatusCode.Accepted);

        again.CaseId.ShouldBe(receipt.CaseId);
        var queued = await CaseAsync(receipt.CaseId);
        (queued.Status, queued.TargetType, queued.Reason, queued.CreatorHandle).ShouldBe(("open", "creator", "impersonation", creator.Handle));
        queued.TargetLabel.ShouldBe($"Profil @{creator.Handle}");

        using var admin = _host.Admin();
        var raw = await admin.GetStringAsync("/api/creators/v1/admin/moderation?limit=200", Ct);
        raw.ShouldNotContain("reporter", Case.Insensitive);
    }

    [Theory]
    [InlineData("planet", "inaccurate", HttpStatusCode.BadRequest)]
    [InlineData("creator", "dislike", HttpStatusCode.BadRequest)]
    public async Task A_report_needs_a_known_target_type_and_reason(string type, string reason, HttpStatusCode expected)
    {
        using var reporter = _host.Traveler();

        (await reporter.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest(type, Guid.NewGuid(), reason), Ct)).StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Reporting_something_that_does_not_exist_is_a_404()
    {
        using var reporter = _host.Traveler();

        (await reporter.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest("tip", Guid.NewGuid(), "inaccurate"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_upheld_report_on_a_tip_hides_it_and_a_decision_without_reasons_is_refused()
    {
        var creator = await _host.FounderAsync(publish: true);
        var poi = await _host.PoiAsync("Château d'If");
        using var admin = _host.Admin();
        using var reporter = _host.Traveler();
        var tip = await CreatorsHost.Read<AdminTipDto>(await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/tips/{poi}", new SetTipRequest("Prenez le premier bateau."), Ct));
        var receipt = await CreatorsHost.Read<ReportReceiptDto>(await reporter.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest("tip", tip.Id, "misleading"), Ct), HttpStatusCode.Accepted);
        var path = $"/api/creators/v1/admin/moderation/{receipt.CaseId}/decision";

        (await admin.PostAsJsonAsync(path, new DecideCaseRequest("upheld", "  "), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CaseAsync(receipt.CaseId)).Status.ShouldBe("open");

        var decided = await CreatorsHost.Read<ModerationCaseDto>(await admin.PostAsJsonAsync(path, new DecideCaseRequest("upheld", "Les horaires cités sont faux."), Ct));

        (decided.Status, decided.Decision, decided.StatementOfReasons).ShouldBe(("decided", "upheld", "Les horaires cités sont faux."));
        (await CreatorsHost.Read<PoiCreatorsDto>(await reporter.GetAsync($"/api/creators/v1/pois/{poi}/contents", Ct))).Items.Single().Tip.ShouldBeNull();
        (await admin.PostAsJsonAsync(path, new DecideCaseRequest("dismissed", null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // decided once
        (await _host.Queued("platform", "AdminActionRecordedV1", "moderation.decide")).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task An_upheld_report_on_a_creator_suspends_it_and_a_dismissed_one_leaves_it_alone()
    {
        var guilty = await _host.FounderAsync(publish: true);
        var innocent = await _host.FounderAsync(publish: true);
        using var admin = _host.Admin();
        using var reporter = _host.Traveler();
        var against = await CreatorsHost.Read<ReportReceiptDto>(await reporter.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest("creator", guilty.Id, "undeclared_ad"), Ct), HttpStatusCode.Accepted);
        var unfounded = await CreatorsHost.Read<ReportReceiptDto>(await reporter.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest("creator", innocent.Id, "other"), Ct), HttpStatusCode.Accepted);

        await admin.PostAsJsonAsync($"/api/creators/v1/admin/moderation/{against.CaseId}/decision", new DecideCaseRequest("upheld", "Partenariat rémunéré non déclaré."), Ct);
        await admin.PostAsJsonAsync($"/api/creators/v1/admin/moderation/{unfounded.CaseId}/decision", new DecideCaseRequest("dismissed", null), Ct);

        (await _host.Detail(guilty.Id)).Status.ShouldBe("suspended");
        (await _host.Detail(innocent.Id)).Status.ShouldBe("published");
        (await _host.Queued("discovery", "CreatorUnpublishedV1", guilty.Id)).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorUnpublishedV1", innocent.Id)).ShouldBe(0);
        (await reporter.GetAsync($"/api/creators/v1/creators/{guilty.Handle}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var open = await CreatorsHost.Read<List<ModerationCaseDto>>(await admin.GetAsync("/api/creators/v1/admin/moderation?status=open&limit=200", Ct));
        open.Select(item => item.Id).ShouldNotContain(against.CaseId);
        (await admin.GetAsync("/api/creators/v1/admin/moderation?status=bogus", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_upheld_report_on_an_association_withdraws_the_place()
    {
        var creator = await _host.FounderAsync(publish: true);
        var poi = await _host.PoiAsync("Basilique Notre-Dame de la Garde");
        var link = await _host.LinkAsync(creator.Id, poi);
        using var admin = _host.Admin();
        using var reporter = _host.Traveler();
        var receipt = await CreatorsHost.Read<ReportReceiptDto>(await reporter.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest("place_link", link.Id, "inaccurate"), Ct), HttpStatusCode.Accepted);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(1);

        await admin.PostAsJsonAsync($"/api/creators/v1/admin/moderation/{receipt.CaseId}/decision", new DecideCaseRequest("upheld", "Le créateur n'y est jamais allé."), Ct);

        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(2);
        (await CreatorsHost.Read<PoiCreatorsDto>(await reporter.GetAsync($"/api/creators/v1/pois/{poi}/contents", Ct))).Total.ShouldBe(0);
        (await CaseAsync(receipt.CaseId)).TargetLabel.ShouldContain("Basilique");
    }
}
