using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Creators.Application;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.IntegrationEvents;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;
using OnVoyage.Platform.Contracts;

namespace Creators.UnitTests;

public sealed class HandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Admin = Guid.NewGuid();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly ICreatorRepository _creators = Substitute.For<ICreatorRepository>();
    private readonly IContentRepository _contents = Substitute.For<IContentRepository>();
    private readonly ICreatorQueries _queries = Substitute.For<ICreatorQueries>();
    private readonly ICreatorsUnitOfWork _unit = Substitute.For<ICreatorsUnitOfWork>();
    private readonly IPoiDirectory _directory = Substitute.For<IPoiDirectory>();
    private IReadOnlyList<object> _committed = [];

    public HandlerTests()
    {
        _unit.When(unit => unit.CommitAsync(Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>())).Do(call => _committed = call.Arg<IReadOnlyList<object>>());
        _queries.GetAdminDetailAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => Detail(call.Arg<Guid>()));
    }

    private static AdminCreatorDetailDto Detail(Guid id) =>
        new(id, null, "marie", "Marie", null, null, [], [], [], [], "draft", true, null, null, null, 0, null, [], [], []);

    private Creator Existing(string status = "draft", bool consent = false, bool account = false, params string[] specialties)
    {
        var creator = Creator.NewDraft(Guid.NewGuid(), new CreatorProfile("marie", "Marie", null, null, [], specialties, [], []), true, _clock.GetUtcNow());
        creator = creator with { Status = status };
        if (consent)
        {
            creator = creator.WithFounderConsent("H-009/marie.pdf", _clock.GetUtcNow(), _clock.GetUtcNow());
        }

        if (account)
        {
            creator = creator.WithAccount(Guid.NewGuid(), _clock.GetUtcNow());
        }

        _creators.FindAsync(creator.Id, Arg.Any<CancellationToken>()).Returns(creator);
        return creator;
    }

    private Task<Result<AdminCreatorDetailDto>> Publish(Creator creator) =>
        CreatorAdminHandler.Handle(new PublishCreatorCommand(Admin, creator.Id), _creators, _queries, _unit, _clock, Ct);

    [Fact]
    public async Task Publishing_without_terms_or_founder_consent_is_refused_with_terms_required_and_nothing_is_saved()
    {
        var creator = Existing(specialties: "nature");

        var result = await Publish(creator);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("terms_required");
        await _creators.DidNotReceiveWithAnyArgs().StageAsync(default!, Ct);
        await _unit.DidNotReceiveWithAnyArgs().CommitAsync(default!, Ct);
    }

    [Fact]
    public async Task Publishing_without_a_specialty_is_refused_even_with_consent()
    {
        var creator = Existing(consent: true);

        (await Publish(creator)).Error!.Code.ShouldBe("specialty_required");
    }

    [Fact]
    public async Task Publishing_a_consenting_creator_stages_it_and_announces_it_with_the_audit_entry()
    {
        var creator = Existing(consent: true, specialties: ["nature", "history"]);

        (await Publish(creator)).IsSuccess.ShouldBeTrue();

        await _creators.Received(1).StageAsync(Arg.Is<Creator>(staged => staged.Id == creator.Id && staged.Status == "published"), Ct);
        _committed.OfType<CreatorPublishedV1>().Single().ShouldSatisfyAllConditions(
            published => published.CreatorId.ShouldBe(creator.Id),
            published => published.Handle.ShouldBe("marie"),
            published => published.Specialties.ShouldBe(["nature", "history"]));
        var audit = _committed.OfType<AdminActionRecordedV1>().Single();
        (audit.Service, audit.Actor, audit.Action, audit.Status).ShouldBe(("creators", Admin.ToString(), "creator.publish", 200));
    }

    [Fact]
    public async Task Publishing_twice_changes_nothing()
    {
        var creator = Existing("published", consent: true, specialties: "nature");

        (await Publish(creator)).IsSuccess.ShouldBeTrue();

        await _unit.DidNotReceiveWithAnyArgs().CommitAsync(default!, Ct);
    }

    [Fact]
    public async Task Unpublishing_publishes_the_reason_and_needs_one()
    {
        var creator = Existing("published", consent: true, specialties: "nature");

        (await CreatorAdminHandler.Handle(new UnpublishCreatorCommand(Admin, creator.Id, " "), _creators, _queries, _unit, _clock, Ct)).Error!.Code.ShouldBe("validation");
        (await CreatorAdminHandler.Handle(new UnpublishCreatorCommand(Admin, creator.Id, "à la demande de la créatrice"), _creators, _queries, _unit, _clock, Ct)).IsSuccess.ShouldBeTrue();

        _committed.OfType<CreatorUnpublishedV1>().Single().Reason.ShouldBe("à la demande de la créatrice");
        await _creators.Received().StageAsync(Arg.Is<Creator>(staged => staged.Status == "draft"), Ct);
    }

    [Fact]
    public async Task Suspending_a_published_creator_withdraws_it_but_a_draft_is_only_flagged()
    {
        var published = Existing("published", consent: true, specialties: "nature");
        var draft = Existing();

        await CreatorAdminHandler.Handle(new SuspendCreatorCommand(Admin, published.Id, "contenu trompeur"), _creators, _queries, _unit, _clock, Ct);
        _committed.OfType<CreatorUnpublishedV1>().Count().ShouldBe(1);

        await CreatorAdminHandler.Handle(new SuspendCreatorCommand(Admin, draft.Id, "doute"), _creators, _queries, _unit, _clock, Ct);
        _committed.OfType<CreatorUnpublishedV1>().ShouldBeEmpty();
        await _creators.Received().StageAsync(Arg.Is<Creator>(staged => staged.Id == draft.Id && staged.Status == "suspended"), Ct);
    }

    [Fact]
    public async Task The_founder_consent_announces_the_terms_only_when_an_account_is_linked()
    {
        var withoutAccount = Existing();
        var withAccount = Existing(account: true);
        var request = new FounderConsentRequest("H-009/marie.pdf", null);

        await CreatorAdminHandler.Handle(new RecordFounderConsentCommand(Admin, withoutAccount.Id, request), _creators, _queries, _unit, _clock, Ct);
        _committed.OfType<CreatorTermsAcceptedV1>().ShouldBeEmpty();

        await CreatorAdminHandler.Handle(new RecordFounderConsentCommand(Admin, withAccount.Id, request), _creators, _queries, _unit, _clock, Ct);
        var accepted = _committed.OfType<CreatorTermsAcceptedV1>().Single();
        (accepted.AccountId, accepted.CreatorId, accepted.TermsVersion).ShouldBe((withAccount.AccountId!.Value, withAccount.Id, "fondateur"));
    }

    [Fact]
    public async Task The_founder_consent_needs_a_document_reference_and_cannot_be_dated_in_the_future()
    {
        var creator = Existing();

        (await CreatorAdminHandler.Handle(new RecordFounderConsentCommand(Admin, creator.Id, new FounderConsentRequest(" ", null)), _creators, _queries, _unit, _clock, Ct)).Error!.Code.ShouldBe("validation");
        (await CreatorAdminHandler.Handle(new RecordFounderConsentCommand(Admin, creator.Id, new FounderConsentRequest("doc", _clock.GetUtcNow().AddDays(1))), _creators, _queries, _unit, _clock, Ct)).Error!.Code.ShouldBe("validation");
    }

    [Fact]
    public async Task Linking_an_account_after_the_consent_announces_the_terms_and_an_account_serves_one_creator()
    {
        var creator = Existing(consent: true);
        var account = Guid.NewGuid();

        await CreatorAdminHandler.Handle(new LinkAccountCommand(Admin, creator.Id, account), _creators, _queries, _unit, _clock, Ct);
        _committed.OfType<CreatorTermsAcceptedV1>().Single().AccountId.ShouldBe(account);

        var other = Existing();
        _creators.FindByAccountAsync(account, Arg.Any<CancellationToken>()).Returns(creator);
        (await CreatorAdminHandler.Handle(new LinkAccountCommand(Admin, other.Id, account), _creators, _queries, _unit, _clock, Ct)).Error!.Code.ShouldBe("account_in_use");
    }

    [Fact]
    public async Task A_taken_handle_is_refused_with_a_free_suggestion()
    {
        var holder = Existing();
        _creators.FindByHandleAsync("Marie", Arg.Any<CancellationToken>()).Returns(holder);
        _creators.HandlesStartingWithAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new HashSet<string>(["marie", "marie2"], StringComparer.OrdinalIgnoreCase));

        var result = await CreatorAdminHandler.Handle(new CreateFounderCommand(Admin, new CreatorProfileRequest("@Marie", "Marie", null, null, null, ["nature"], null, null)), _creators, _queries, _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("handle_taken");
        result.Error.Suggestion.ShouldBe("Marie3");
        await _unit.DidNotReceiveWithAnyArgs().CommitAsync(default!, Ct);
    }

    [Fact]
    public async Task A_published_profile_cannot_lose_its_last_specialty()
    {
        var creator = Existing("published", consent: true, specialties: "nature");

        var result = await CreatorAdminHandler.Handle(new UpdateCreatorCommand(Admin, creator.Id, new CreatorProfileRequest("marie", "Marie", null, null, null, [], null, null)), _creators, _queries, _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("specialty_required");
    }

    [Fact]
    public async Task Updating_a_published_profile_announces_the_new_public_data()
    {
        var creator = Existing("published", consent: true, specialties: "nature");

        await CreatorAdminHandler.Handle(new UpdateCreatorCommand(Admin, creator.Id, new CreatorProfileRequest("marie", "Marie G.", null, null, null, ["nature", "culture"], null, null)), _creators, _queries, _unit, _clock, Ct);

        _committed.OfType<CreatorPublishedV1>().Single().ShouldSatisfyAllConditions(
            published => published.DisplayName.ShouldBe("Marie G."),
            published => published.Specialties.ShouldBe(["nature", "culture"]));
    }

    [Fact]
    public async Task A_content_is_referenced_by_its_url_once()
    {
        var creator = Existing();
        var request = new AddContentRequest("https://youtu.be/dQw4w9WgXcQ?si=x", "Marseille en 48 h", null, null, null, 600, null, false, [new ChapterDto(135, "Le Panier")]);

        var added = await CreatorContentHandler.Handle(new AddContentCommand(Admin, creator.Id, request), _creators, _contents, _unit, _clock, Ct);

        added.Value!.ShouldSatisfyAllConditions(
            content => content.Platform.ShouldBe("youtube"),
            content => content.Permalink.ShouldBe("https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
            content => content.Kind.ShouldBe("video"),
            content => content.Chapters.ShouldBe([new ChapterDto(135, "Le Panier")]));

        _contents.ContentExistsAsync("youtube", "dQw4w9WgXcQ", Arg.Any<CancellationToken>()).Returns(true);
        (await CreatorContentHandler.Handle(new AddContentCommand(Admin, creator.Id, request), _creators, _contents, _unit, _clock, Ct)).Error!.Code.ShouldBe("content_exists");
        (await CreatorContentHandler.Handle(new AddContentCommand(Admin, creator.Id, request with { Url = "https://vimeo.com/1" }), _creators, _contents, _unit, _clock, Ct)).Error!.Code.ShouldBe("invalid_content_url");
    }

    [Fact]
    public async Task A_validated_link_is_announced_and_a_proposed_one_is_not()
    {
        var creator = Existing();
        var poi = Guid.NewGuid();
        _directory.FindAsync(poi, Arg.Any<CancellationToken>()).Returns(new PoiEntry(poi, Guid.NewGuid(), "marseille", "Fort Saint-Jean", null, [], "Marseille", 80, true, 1));

        await CreatorContentHandler.Handle(new AddPlaceLinkCommand(Admin, creator.Id, new AddPlaceLinkRequest(poi, null, null, "proposed")), _creators, _contents, _directory, _unit, _clock, Ct);
        _committed.OfType<CreatorPlaceLinkChangedV1>().ShouldBeEmpty();

        await CreatorContentHandler.Handle(new AddPlaceLinkCommand(Admin, creator.Id, new AddPlaceLinkRequest(poi, null, null, null)), _creators, _contents, _directory, _unit, _clock, Ct);
        var changed = _committed.OfType<CreatorPlaceLinkChangedV1>().Single();
        (changed.CreatorId, changed.PoiId, changed.Kind, changed.Status).ShouldBe((creator.Id, poi, "tip", "validated"));
    }

    [Fact]
    public async Task A_link_to_an_unknown_place_or_to_somebody_elses_content_is_refused()
    {
        var creator = Existing();
        var poi = Guid.NewGuid();

        (await CreatorContentHandler.Handle(new AddPlaceLinkCommand(Admin, creator.Id, new AddPlaceLinkRequest(poi, null, null, null)), _creators, _contents, _directory, _unit, _clock, Ct)).Error!.Code.ShouldBe("place_not_found");

        _directory.FindAsync(poi, Arg.Any<CancellationToken>()).Returns(new PoiEntry(poi, Guid.NewGuid(), "marseille", "Fort", null, [], null, 1, true, 1));
        var foreign = new ContentItem(Guid.NewGuid(), Guid.NewGuid(), "youtube", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "t", null, null, null, "video", null, [], false, "imported");
        _contents.FindContentAsync(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);
        (await CreatorContentHandler.Handle(new AddPlaceLinkCommand(Admin, creator.Id, new AddPlaceLinkRequest(poi, foreign.Id, 30, null)), _creators, _contents, _directory, _unit, _clock, Ct)).Error!.Code.ShouldBe("content_not_found");
    }

    [Fact]
    public async Task Hiding_a_content_withdraws_the_places_it_supported()
    {
        var creator = Existing();
        var content = new ContentItem(Guid.NewGuid(), creator.Id, "youtube", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "t", null, null, null, "video", null, [], true, "imported");
        _contents.FindContentAsync(content.Id, Arg.Any<CancellationToken>()).Returns(content);
        var validated = new PlaceLink(Guid.NewGuid(), creator.Id, Guid.NewGuid(), content.Id, 135, 1, "validated", _clock.GetUtcNow(), _clock.GetUtcNow());
        var proposed = validated with { Id = Guid.NewGuid(), PoiId = Guid.NewGuid(), Status = "proposed" };
        _contents.ListLinksOfContentAsync(content.Id, Arg.Any<CancellationToken>()).Returns([validated, proposed]);

        await CreatorContentHandler.Handle(new UpdateContentCommand(Admin, creator.Id, content.Id, new UpdateContentRequest("t", null, null, null, true, null, "hidden")), _contents, _unit, _clock, Ct);

        var removed = _committed.OfType<CreatorPlaceLinkChangedV1>().Single();
        (removed.PoiId, removed.ContentId, removed.Status, removed.IsCommercial, removed.Kind).ShouldBe((validated.PoiId, content.Id, "removed", true, "video"));
    }

    [Fact]
    public async Task Following_publishes_once_and_repeating_it_does_nothing()
    {
        var traveler = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var follows = Substitute.For<IFollowRepository>();
        _queries.IsPublishedAsync(creatorId, Arg.Any<CancellationToken>()).Returns(true);
        follows.StageAsync(traveler, creatorId, true, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true, false);

        var first = await PublicCreatorHandler.Handle(new SetFollowCommand(traveler, creatorId, true), _queries, follows, _unit, _clock, Ct);
        var second = await PublicCreatorHandler.Handle(new SetFollowCommand(traveler, creatorId, true), _queries, follows, _unit, _clock, Ct);

        first.Value!.Following.ShouldBeTrue();
        second.Value!.Following.ShouldBeTrue();
        await _unit.Received(1).CommitAsync(Arg.Any<IReadOnlyList<object>>(), Ct);
        _committed.OfType<FollowChangedV1>().Single().ShouldSatisfyAllConditions(
            followed => followed.TravelerId.ShouldBe(traveler),
            followed => followed.CreatorId.ShouldBe(creatorId),
            followed => followed.Following.ShouldBeTrue());
    }

    [Fact]
    public async Task An_unpublished_creator_cannot_be_followed()
    {
        var follows = Substitute.For<IFollowRepository>();

        var result = await PublicCreatorHandler.Handle(new SetFollowCommand(Guid.NewGuid(), Guid.NewGuid(), true), _queries, follows, _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("creator_not_found");
        await follows.DidNotReceiveWithAnyArgs().StageAsync(default, default, default, default, Ct);
    }

    [Fact]
    public async Task A_report_is_recorded_once_per_reporter_target_and_reason()
    {
        var cases = Substitute.For<IModerationRepository>();
        var traveler = Guid.NewGuid();
        var target = Guid.NewGuid();
        cases.TargetExistsAsync("tip", target, Arg.Any<CancellationToken>()).Returns(true);

        var first = await ModerationHandler.Handle(new ReportCommand(traveler, new ReportRequest("tip", target, "inaccurate")), cases, _unit, _clock, Ct);
        cases.FindOpenAsync(traveler, "tip", target, "inaccurate", Arg.Any<CancellationToken>()).Returns(ModerationCase.Open(first.Value!.CaseId, "tip", target, "inaccurate", traveler, _clock.GetUtcNow()));
        var second = await ModerationHandler.Handle(new ReportCommand(traveler, new ReportRequest("tip", target, "inaccurate")), cases, _unit, _clock, Ct);

        second.Value!.CaseId.ShouldBe(first.Value.CaseId);
        await cases.Received(1).StageAsync(Arg.Any<ModerationCase>(), Ct);
    }

    [Theory]
    [InlineData("planet", "inaccurate")]
    [InlineData("tip", "dislike")]
    public async Task A_report_with_an_unknown_target_type_or_reason_is_refused(string type, string reason)
    {
        var result = await ModerationHandler.Handle(new ReportCommand(Guid.NewGuid(), new ReportRequest(type, Guid.NewGuid(), reason)), Substitute.For<IModerationRepository>(), _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("validation");
    }

    [Fact]
    public async Task Upholding_a_report_on_a_content_hides_it_and_withdraws_its_validated_places()
    {
        var cases = Substitute.For<IModerationRepository>();
        var creator = Existing();
        var content = new ContentItem(Guid.NewGuid(), creator.Id, "instagram", "C1a2B3c4D5e", "https://www.instagram.com/reel/C1a2B3c4D5e/", "t", null, null, null, "video", null, [], false, "imported");
        var report = ModerationCase.Open(Guid.NewGuid(), "content", content.Id, "undeclared_ad", Guid.NewGuid(), _clock.GetUtcNow());
        cases.FindAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _contents.FindContentAsync(content.Id, Arg.Any<CancellationToken>()).Returns(content);
        _contents.ListLinksOfContentAsync(content.Id, Arg.Any<CancellationToken>()).Returns([new PlaceLink(Guid.NewGuid(), creator.Id, Guid.NewGuid(), content.Id, null, 1, "validated", _clock.GetUtcNow(), _clock.GetUtcNow())]);
        _queries.GetModerationAsync(report.Id, Arg.Any<CancellationToken>()).Returns(new ModerationCaseDto(report.Id, "content", content.Id, "x", creator.Id, "marie", "undeclared_ad", "decided", "upheld", "s", _clock.GetUtcNow(), _clock.GetUtcNow()));

        var withoutStatement = await ModerationHandler.Handle(new DecideCaseCommand(Admin, report.Id, "upheld", null), cases, _creators, _contents, _queries, _unit, _clock, Ct);
        withoutStatement.Error!.Code.ShouldBe("statement_required");

        var decided = await ModerationHandler.Handle(new DecideCaseCommand(Admin, report.Id, "upheld", "Collaboration commerciale non déclarée."), cases, _creators, _contents, _queries, _unit, _clock, Ct);

        decided.IsSuccess.ShouldBeTrue();
        await _contents.Received().StageContentAsync(Arg.Is<ContentItem>(staged => staged.Status == "hidden"), Ct);
        _committed.OfType<CreatorPlaceLinkChangedV1>().Single().Status.ShouldBe("removed");
        _committed.OfType<AdminActionRecordedV1>().Single().Action.ShouldBe("moderation.decide");
    }

    [Fact]
    public async Task Dismissing_a_report_takes_nothing_down()
    {
        var cases = Substitute.For<IModerationRepository>();
        var report = ModerationCase.Open(Guid.NewGuid(), "creator", Guid.NewGuid(), "other", Guid.NewGuid(), _clock.GetUtcNow());
        cases.FindAsync(report.Id, Arg.Any<CancellationToken>()).Returns(report);
        _queries.GetModerationAsync(report.Id, Arg.Any<CancellationToken>()).Returns(new ModerationCaseDto(report.Id, "creator", report.TargetId, "x", null, null, "other", "decided", "dismissed", null, _clock.GetUtcNow(), _clock.GetUtcNow()));

        await ModerationHandler.Handle(new DecideCaseCommand(Admin, report.Id, "dismissed", null), cases, _creators, _contents, _queries, _unit, _clock, Ct);

        await _creators.DidNotReceiveWithAnyArgs().StageAsync(default!, Ct);
        _committed.OfType<CreatorUnpublishedV1>().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_catalog_projection_is_applied_without_its_coordinates()
    {
        var writer = Substitute.For<IPoiDirectoryWriter>();
        var poi = Guid.NewGuid();
        var changed = new PoiProjectionChangedV1(Guid.NewGuid(), _clock.GetUtcNow(), poi, 3, Guid.NewGuid(), "marseille", "fort-saint-jean", "Fort Saint-Jean", "Fort Saint-Jean (en)", ["Le Fort"], "Marseille", 43.29, 5.36, 80, false, 0.9f, 3, new Dictionary<string, float>(), ["fragile"], true);

        await PoiProjectionChangedHandler.Handle(changed, writer, Ct);

        await writer.Received().ApplyAsync(Arg.Is<PoiEntry>(entry => entry.PoiId == poi && entry.Version == 3 && entry.NameFr == "Fort Saint-Jean" && entry.Aliases.Count == 1 && entry.City == "Marseille" && entry.IsPublished), Ct);
    }

    [Fact]
    public async Task A_deletion_request_deletes_then_confirms_to_platform_for_creators()
    {
        var store = Substitute.For<IDataRightsStore>();
        var traveler = Guid.NewGuid();

        var confirmation = await TravelerDeletionRequestedHandler.Handle(new TravelerDeletionRequestedV1(Guid.NewGuid(), _clock.GetUtcNow(), traveler, _clock.GetUtcNow()), store, _clock, Ct);

        await store.Received(1).DeleteTravelerAsync(traveler, Arg.Any<DateTimeOffset>(), Ct);
        (confirmation.TravelerId, confirmation.Service).ShouldBe((traveler, "creators"));
    }

    [Fact]
    public async Task An_export_request_writes_one_part_and_announces_its_path()
    {
        var store = Substitute.For<IDataRightsStore>();
        var traveler = Guid.NewGuid();
        var export = Guid.NewGuid();
        store.ExportAsync(traveler, Arg.Any<CancellationToken>()).Returns("{}");
        store.WritePartAsync(export, "{}", Arg.Any<CancellationToken>()).Returns("abc/creators.json");

        var ready = await TravelerExportRequestedHandler.Handle(new TravelerExportRequestedV1(Guid.NewGuid(), _clock.GetUtcNow(), export, traveler), store, _clock, Ct);

        (ready.ExportId, ready.Service, ready.Path).ShouldBe((export, "creators", "abc/creators.json"));
    }

    [Fact]
    public async Task Public_lists_clamp_the_limit_and_refuse_a_bad_cursor()
    {
        _queries.ListPublishedAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new CreatorListDto([], null));

        await PublicCreatorHandler.Handle(new ListCreatorsQuery("marseille", "nature", "40", 500), _queries, Ct);
        await _queries.Received().ListPublishedAsync("marseille", "nature", 40, 50, Ct);

        (await PublicCreatorHandler.Handle(new ListCreatorsQuery(null, null, "-3", null), _queries, Ct)).Error!.Code.ShouldBe("validation");
        (await PublicCreatorHandler.Handle(new ListCreatorsQuery(null, null, "abc", null), _queries, Ct)).Error!.Code.ShouldBe("validation");
    }

    [Fact]
    public async Task A_page_is_asked_by_a_valid_handle_only()
    {
        (await PublicCreatorHandler.Handle(new GetCreatorPageQuery("x", null), _queries, Ct)).Error!.Code.ShouldBe("creator_not_found");
        await _queries.DidNotReceiveWithAnyArgs().GetPageAsync(default!, default, Ct);
    }
}
