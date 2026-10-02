using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace Creators.UnitTests;

public sealed class StudioHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly ICreatorRepository _creators = Substitute.For<ICreatorRepository>();
    private readonly ICreatorQueries _queries = Substitute.For<ICreatorQueries>();
    private readonly ICreatorsUnitOfWork _unit = Substitute.For<ICreatorsUnitOfWork>();
    private readonly ICreatorTerms _terms = Substitute.For<ICreatorTerms>();
    private IReadOnlyList<object> _committed = [];

    public StudioHandlerTests()
    {
        _terms.CurrentVersion.Returns("2026-10");
        _unit.When(unit => unit.CommitAsync(Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>())).Do(call => _committed = call.Arg<IReadOnlyList<object>>());
        _creators.HandlesStartingWithAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new HashSet<string>());
    }

    [Fact]
    public async Task Signing_up_with_the_current_terms_creates_a_draft_and_announces_the_acceptance()
    {
        var account = Guid.NewGuid();

        var result = await StudioHandler.Handle(new StudioSignupCommand(account, new StudioSignupRequest("@marie_m", "Marie", "2026-10")), _creators, _terms, _unit, _clock, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.TermsAccepted.ShouldBeTrue();
        await _creators.Received(1).StageAsync(Arg.Is<Creator>(creator => creator.AccountId == account && creator.Handle == "marie_m" && !creator.Founding && creator.Status == "draft"), Ct);
        var accepted = _committed.OfType<OnVoyage.Creators.Contracts.CreatorTermsAcceptedV1>().ShouldHaveSingleItem();
        accepted.AccountId.ShouldBe(account);
        accepted.TermsVersion.ShouldBe("2026-10");
    }

    [Theory]
    [InlineData("2001-01")]
    [InlineData("")]
    public async Task Signing_up_with_any_other_version_of_the_terms_saves_nothing(string version)
    {
        var result = await StudioHandler.Handle(new StudioSignupCommand(Guid.NewGuid(), new StudioSignupRequest("marie_m", "Marie", version)), _creators, _terms, _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("terms_required");
        await _creators.DidNotReceiveWithAnyArgs().StageAsync(default!, Ct);
        await _unit.DidNotReceiveWithAnyArgs().CommitAsync(default!, Ct);
    }

    [Fact]
    public async Task A_taken_handle_is_refused_with_a_free_variant()
    {
        _creators.FindByHandleAsync("marie", Arg.Any<CancellationToken>()).Returns(Creator.NewDraft(Guid.NewGuid(), new CreatorProfile("Marie", "Marie", null, null, [], [], [], []), false, _clock.GetUtcNow()));
        _creators.HandlesStartingWithAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new HashSet<string>(["marie", "marie2"], StringComparer.OrdinalIgnoreCase));

        var result = await StudioHandler.Handle(new StudioSignupCommand(Guid.NewGuid(), new StudioSignupRequest("marie", "Marie", "2026-10")), _creators, _terms, _unit, _clock, Ct);

        result.Error!.Code.ShouldBe("handle_taken");
        result.Error.Suggestion.ShouldBe("marie3");
    }

    [Fact]
    public async Task A_suspended_creator_cannot_publish_or_edit()
    {
        var account = Guid.NewGuid();
        var suspended = Creator.NewDraft(Guid.NewGuid(), new CreatorProfile("marie", "Marie", null, null, [], ["nature"], [], []), false, _clock.GetUtcNow()).WithAccount(account, _clock.GetUtcNow()).WithTermsAccepted("2026-10", _clock.GetUtcNow(), _clock.GetUtcNow()).Suspended(_clock.GetUtcNow());
        _creators.FindByAccountAsync(account, Arg.Any<CancellationToken>()).Returns(suspended);

        var published = await StudioHandler.Handle(new PublishStudioCommand(account), _creators, _queries, _unit, _clock, Ct);

        published.Error!.Code.ShouldBe("creator_suspended");
        await _unit.DidNotReceiveWithAnyArgs().CommitAsync(default!, Ct);
    }

    [Fact]
    public void The_follower_count_is_hidden_below_the_public_threshold()
    {
        StudioDto(followers: 19).FollowerCount.ShouldBeNull();
        StudioDto(followers: 19).IsNew.ShouldBeTrue();
        StudioDto(followers: 20).FollowerCount.ShouldBe(20);
        StudioDto(followers: 20).IsNew.ShouldBeFalse();
    }

    private static StudioProfileDto StudioDto(int followers) =>
        StudioHandler.ToProfile(new AdminCreatorDetailDto(Guid.NewGuid(), Guid.NewGuid(), "marie", "Marie", null, null, [], [], [], [], "published", false, "2026-10", null, null, followers, null, [], [], []));
}
