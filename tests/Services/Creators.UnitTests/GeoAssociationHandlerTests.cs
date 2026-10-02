using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.Creators.Application;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;
using Wolverine;

namespace Creators.UnitTests;

/// <summary>The pipeline of F-28 around the matcher: what becomes a proposal, what is suggested to the editors, what is never done.</summary>
public sealed class GeoAssociationHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly ICreatorRepository _creators = Substitute.For<ICreatorRepository>();
    private readonly IContentRepository _contents = Substitute.For<IContentRepository>();
    private readonly IPoiDirectory _directory = Substitute.For<IPoiDirectory>();
    private readonly IUnmatchedMentionRepository _unmatched = Substitute.For<IUnmatchedMentionRepository>();
    private readonly IPlaceMentionExtractor _extractor = Substitute.For<IPlaceMentionExtractor>();
    private readonly IGeoAssociationSettings _settings = Substitute.For<IGeoAssociationSettings>();
    private readonly ICreatorsUnitOfWork _unit = Substitute.For<ICreatorsUnitOfWork>();
    private readonly List<PlaceLink> _staged = [];
    private IReadOnlyList<object> _events = [];
    private readonly Creator _creator;
    private readonly ContentItem _content;

    public GeoAssociationHandlerTests()
    {
        _settings.Enabled.Returns(true);
        _settings.BulkThreshold.Returns(0.9);
        _settings.MinProposal.Returns(0.4);
        _settings.MaxPerContent.Returns(50);
        _settings.SuggestionConfidence.Returns(0.7);
        _creator = Creator.NewDraft(Guid.NewGuid(), new CreatorProfile("marie", "Marie", null, null, ["fr"], ["history"], [GeoCorpus.Marseille], []), false, _clock.GetUtcNow());
        _content = new ContentItem(Guid.NewGuid(), _creator.Id, "youtube", "abcdefghijk", "https://www.youtube.com/watch?v=abcdefghijk", "Marseille", "Le Vieux-Port, Gordes", null, 600, "video", null, [], false, ContentStatuses.Imported);
        _creators.FindAsync(_creator.Id, Arg.Any<CancellationToken>()).Returns(_creator);
        _contents.FindContentAsync(_content.Id, Arg.Any<CancellationToken>()).Returns(_content);
        _contents.ListLinksAsync(_creator.Id, Arg.Any<CancellationToken>()).Returns([]);
        _directory.DestinationsOfAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        _directory.FindCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => GeoCorpus.Candidates(call.Arg<string>()));
        _unmatched.StageIfNewAsync(default, default, default!, default!, default, default, default, default).ReturnsForAnyArgs(true);
        _contents.When(contents => contents.StageLinkAsync(Arg.Any<PlaceLink>(), Arg.Any<CancellationToken>())).Do(call => _staged.Add(call.Arg<PlaceLink>()));
        _unit.When(unit => unit.CommitAsync(Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>())).Do(call => _events = call.Arg<IReadOnlyList<object>>());
    }

    private Task<Result<bool>> Analyze() =>
        GeoAssociationHandler.Handle(new AnalyzeContentCommand(_content.Id), _creators, _contents, _directory, _unmatched, _extractor, _settings, _unit, _clock, NullLogger<AnalyzeContentCommand>.Instance, Ct);

    private static PlaceMention Mention(string name, double confidence = 0.8, string? city = null) => new(name, null, city, $"… {name} …", confidence);

    [Fact]
    public async Task What_the_assistant_finds_becomes_proposals_never_validated_links_and_nothing_is_published()
    {
        _extractor.ExtractAsync(Arg.Any<GeotagInput>(), Arg.Any<CancellationToken>()).Returns([Mention("Vieux-Port"), Mention("Gordes")]);

        var result = await Analyze();

        result.IsSuccess.ShouldBeTrue();
        _staged.Select(link => link.Status).ShouldAllBe(status => status == PlaceLinkStatuses.Proposed);
        _staged.Select(link => link.PoiId).ShouldBe([GeoCorpus.Id("Vieux-Port"), GeoCorpus.Id("Gordes")], ignoreOrder: true);
        _staged.ShouldAllBe(link => link.ValidatedAt == null && link.Confidence > 0.4);
        _events.OfType<CreatorPlaceLinkChangedV1>().ShouldBeEmpty(); // a proposal is not announced to anybody
        await _contents.Received(1).StageContentAsync(Arg.Is<ContentItem>(content => content.GeotaggedAt == _clock.GetUtcNow()), Ct);
    }

    [Fact]
    public async Task A_place_already_proposed_validated_or_rejected_is_not_proposed_again()
    {
        _extractor.ExtractAsync(Arg.Any<GeotagInput>(), Arg.Any<CancellationToken>()).Returns([Mention("Gordes")]);
        _contents.FindLinkAsync(_creator.Id, GeoCorpus.Id("Gordes"), _content.Id, null, Arg.Any<CancellationToken>())
            .Returns(new PlaceLink(Guid.NewGuid(), _creator.Id, GeoCorpus.Id("Gordes"), _content.Id, null, 0.9, PlaceLinkStatuses.Rejected, null, _clock.GetUtcNow()));

        await Analyze();

        _staged.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_place_the_catalog_does_not_have_is_suggested_once_to_the_editors_with_the_destination_of_the_content()
    {
        _extractor.ExtractAsync(Arg.Any<GeotagInput>(), Arg.Any<CancellationToken>()).Returns([Mention("Vieux-Port"), Mention("Fort Saint-Jean"), Mention("Gordes"), Mention("Cap Canaille", 0.9, "Cassis"), Mention("Peut-être", 0.5)]);
        _unmatched.StageIfNewAsync(_creator.Id, _content.Id, "cap canaille", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true, false);

        await Analyze();
        var first = _events.OfType<PlaceSuggestedV1>().ToList();
        await Analyze();
        var second = _events.OfType<PlaceSuggestedV1>().ToList();

        first.Count.ShouldBe(1);
        (first[0].Name, first[0].City, first[0].CreatorId, first[0].ContentId).ShouldBe(("Cap Canaille", "Cassis", _creator.Id, _content.Id));
        first[0].DestinationSlug.ShouldBe("marseille"); // where the places it did match are
        second.ShouldBeEmpty(); // already suggested for this content
    }

    [Fact]
    public async Task A_mention_the_reader_is_not_sure_is_a_place_is_never_suggested()
    {
        _extractor.ExtractAsync(Arg.Any<GeotagInput>(), Arg.Any<CancellationToken>()).Returns([Mention("Quechua", 0.5)]);

        await Analyze();

        _events.OfType<PlaceSuggestedV1>().ShouldBeEmpty();
        await _unmatched.DidNotReceiveWithAnyArgs().StageIfNewAsync(default, default, default!, default!, default, default, default, Ct);
    }

    [Fact]
    public async Task Chapters_take_their_timestamp_and_a_chapter_nobody_mentioned_still_gets_a_proposal()
    {
        var content = _content with { Chapters = [new Chapter(135, "Gordes"), new Chapter(340, "Cassis")] };
        _contents.FindContentAsync(_content.Id, Arg.Any<CancellationToken>()).Returns(content);
        _extractor.ExtractAsync(Arg.Any<GeotagInput>(), Arg.Any<CancellationToken>()).Returns([Mention("Gordes")]);

        await Analyze();

        _staged.Select(link => (link.PoiId, link.StartSeconds)).ShouldBe([(GeoCorpus.Id("Gordes"), (int?)135), (GeoCorpus.Id("Cassis"), (int?)340)], ignoreOrder: true);
    }

    [Fact]
    public async Task Without_a_model_nothing_is_analysed_and_nothing_fails()
    {
        _settings.Enabled.Returns(false);

        var result = await Analyze();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeFalse();
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, Ct);
        await _contents.DidNotReceiveWithAnyArgs().StageContentAsync(default!, Ct);
    }

    [Fact]
    public async Task A_failing_model_leaves_the_content_not_analysed_so_it_can_be_asked_again()
    {
        _extractor.ExtractAsync(Arg.Any<GeotagInput>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<PlaceMention>>(_ => throw new HttpRequestException("503"));

        var result = await Analyze();

        result.Error!.Code.ShouldBe("analysis_failed");
        await _contents.DidNotReceiveWithAnyArgs().StageContentAsync(default!, Ct);
        await _unit.DidNotReceiveWithAnyArgs().CommitAsync(default!, Ct);
    }

    [Fact]
    public async Task A_hidden_or_removed_content_is_not_analysed()
    {
        _contents.FindContentAsync(_content.Id, Arg.Any<CancellationToken>()).Returns(_content with { Status = ContentStatuses.Hidden });

        (await Analyze()).Value.ShouldBeFalse();

        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, Ct);
    }

    [Fact]
    public async Task No_more_than_the_maximum_number_of_proposals_are_made_for_one_content()
    {
        _settings.MaxPerContent.Returns(1);
        _extractor.ExtractAsync(Arg.Any<GeotagInput>(), Arg.Any<CancellationToken>()).Returns([Mention("Gordes"), Mention("Cassis"), Mention("Arles")]);

        await Analyze();

        _staged.Count.ShouldBe(1);
    }

    // ---- review

    [Fact]
    public async Task Tout_valider_never_validates_below_the_bulk_threshold_even_when_asked_to()
    {
        var creator = _creator.WithAccount(Guid.NewGuid(), _clock.GetUtcNow()).WithTermsAccepted("2026-10", _clock.GetUtcNow(), _clock.GetUtcNow());
        _creators.FindByAccountAsync(creator.AccountId!.Value, Arg.Any<CancellationToken>()).Returns(creator);
        PlaceLink Proposal(double confidence, string poi) => new(Guid.NewGuid(), creator.Id, GeoCorpus.Id(poi), null, null, confidence, PlaceLinkStatuses.Proposed, null, _clock.GetUtcNow());
        var sure = Proposal(0.97, "Gordes");
        var unsure = Proposal(0.7, "Cassis");
        var other = Proposal(0.99, "Arles") with { CreatorId = Guid.NewGuid() };
        _contents.ListLinksAsync(creator.Id, Arg.Any<CancellationToken>()).Returns([sure, unsure, other]);

        var result = await GeoAssociationHandler.Handle(new ValidateProposalsCommand(creator.AccountId.Value, new ReviewPlaceLinksRequest(null, 0.1)), _creators, _contents, _settings, _unit, _clock, Ct);

        result.Value!.Validated.ShouldBe(1);
        _staged.Select(link => link.Id).ShouldBe([sure.Id]);
        _staged.Single().Status.ShouldBe(PlaceLinkStatuses.Validated);
        _events.OfType<CreatorPlaceLinkChangedV1>().Single().PoiId.ShouldBe(GeoCorpus.Id("Gordes"));
    }

    [Fact]
    public async Task Validating_by_identifier_reaches_only_the_proposals_of_this_creator()
    {
        var creator = _creator.WithAccount(Guid.NewGuid(), _clock.GetUtcNow()).WithTermsAccepted("2026-10", _clock.GetUtcNow(), _clock.GetUtcNow());
        _creators.FindByAccountAsync(creator.AccountId!.Value, Arg.Any<CancellationToken>()).Returns(creator);
        var mine = new PlaceLink(Guid.NewGuid(), creator.Id, GeoCorpus.Id("Gordes"), null, null, 0.5, PlaceLinkStatuses.Proposed, null, _clock.GetUtcNow());
        var rejected = mine with { Id = Guid.NewGuid(), PoiId = GeoCorpus.Id("Cassis"), Status = PlaceLinkStatuses.Rejected };
        var notMine = mine with { Id = Guid.NewGuid(), CreatorId = Guid.NewGuid() };
        _contents.ListLinksAsync(creator.Id, Arg.Any<CancellationToken>()).Returns([mine, rejected]);

        var result = await GeoAssociationHandler.Handle(new ValidateProposalsCommand(creator.AccountId.Value, new ReviewPlaceLinksRequest([mine.Id, rejected.Id, notMine.Id], null)), _creators, _contents, _settings, _unit, _clock, Ct);

        result.Value!.Validated.ShouldBe(1);
        _staged.Select(link => link.Id).ShouldBe([mine.Id]);
    }

    [Fact]
    public async Task A_suspended_creator_cannot_review()
    {
        var creator = _creator.WithAccount(Guid.NewGuid(), _clock.GetUtcNow()).Suspended(_clock.GetUtcNow());
        _creators.FindByAccountAsync(creator.AccountId!.Value, Arg.Any<CancellationToken>()).Returns(creator);

        var result = await GeoAssociationHandler.Handle(new ValidateProposalsCommand(creator.AccountId.Value, new ReviewPlaceLinksRequest(null, 0.95)), _creators, _contents, _settings, _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("creator_suspended");
        await _unit.DidNotReceiveWithAnyArgs().CommitAsync(default!, Ct);
    }

    [Fact]
    public async Task Validation_without_any_selection_is_refused()
    {
        var creator = _creator.WithAccount(Guid.NewGuid(), _clock.GetUtcNow());
        _creators.FindByAccountAsync(creator.AccountId!.Value, Arg.Any<CancellationToken>()).Returns(creator);

        var result = await GeoAssociationHandler.Handle(new ValidateProposalsCommand(creator.AccountId.Value, new ReviewPlaceLinksRequest(null, null)), _creators, _contents, _settings, _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("validation");
    }

    [Fact]
    public async Task Asking_for_an_analysis_sends_one_message_per_content_not_analysed_yet()
    {
        var creator = _creator.WithAccount(Guid.NewGuid(), _clock.GetUtcNow());
        _creators.FindByAccountAsync(creator.AccountId!.Value, Arg.Any<CancellationToken>()).Returns(creator);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _contents.ListContentIdsToAnalyzeAsync(creator.Id, false, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ids);
        var bus = Substitute.For<IMessageBus>();

        var result = await GeoAssociationHandler.Handle(new AnalyzeContentsCommand(creator.AccountId.Value, false), _creators, _contents, bus, Ct);

        result.Value!.Contents.ShouldBe(2);
        await bus.Received(1).PublishAsync(new AnalyzeContentCommand(ids[0]));
        await bus.Received(1).PublishAsync(new AnalyzeContentCommand(ids[1]));
    }
}
