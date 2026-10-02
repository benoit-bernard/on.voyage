using NSubstitute;
using OnVoyage.Creators.Contracts;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.Discovery.Application.IntegrationEvents;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;

namespace Discovery.UnitTests;

public sealed class CreatorHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_published_creator_is_projected_as_published_and_an_unpublished_one_keeps_only_the_flag()
    {
        var writer = Substitute.For<ICreatorProjectionWriter>();
        var id = Guid.NewGuid();

        await CreatorPublishedHandler.Handle(new CreatorPublishedV1(Guid.NewGuid(), Now, id, "marie", "Marie", "a/marie.jpg", ["history"]), writer, Ct);
        await CreatorUnpublishedHandler.Handle(new CreatorUnpublishedV1(Guid.NewGuid(), Now.AddMinutes(1), id, "marie", "moderation"), writer, Ct);

        await writer.Received(1).ApplyCreatorAsync(Arg.Is<CreatorProjection>(c => c.CreatorId == id && c.IsPublished && c.DisplayName == "Marie" && c.Specialties.Count == 1), Ct);
        await writer.Received(1).ApplyCreatorAsync(Arg.Is<CreatorProjection>(c => c.CreatorId == id && !c.IsPublished), Ct);
    }

    [Theory]
    [InlineData("validated", true)]
    [InlineData("removed", false)]
    public async Task Only_a_validated_link_counts_and_the_advertising_flag_travels_with_it(string status, bool validated)
    {
        var writer = Substitute.For<ICreatorProjectionWriter>();
        var creator = Guid.NewGuid();
        var poi = Guid.NewGuid();

        await CreatorPlaceLinkChangedHandler.Handle(new CreatorPlaceLinkChangedV1(Guid.NewGuid(), Now, creator, poi, null, "tip", true, status), writer, Ct);

        await writer.Received(1).ApplyLinkAsync(Arg.Is<CreatorLinkProjection>(l => l.CreatorId == creator && l.PoiId == poi && l.ContentId == null && l.Kind == "tip" && l.IsCommercial && l.Validated == validated), Ct);
    }

    [Fact]
    public void The_follow_interaction_id_is_stable_per_pair_and_differs_between_pairs()
    {
        var traveler = Guid.NewGuid();
        var creator = Guid.NewGuid();

        FollowChangedHandler.InteractionId(traveler, creator).ShouldBe(FollowChangedHandler.InteractionId(traveler, creator));
        FollowChangedHandler.InteractionId(traveler, creator).ShouldNotBe(FollowChangedHandler.InteractionId(traveler, Guid.NewGuid()));
        FollowChangedHandler.InteractionId(traveler, creator).ShouldNotBe(FollowChangedHandler.InteractionId(Guid.NewGuid(), creator));
    }

    [Fact]
    public async Task The_creators_list_refuses_a_limit_outside_one_to_fifty_without_reading_anything()
    {
        var creators = Substitute.For<ICreatorReader>();

        var result = await GetCreatorsForMeHandler.Handle(new GetCreatorsForMeQuery(Guid.NewGuid(), "marseille", 51), Substitute.For<IDiscoveryStore>(), Substitute.For<ITravelerReader>(), creators, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("validation");
        await creators.DidNotReceiveWithAnyArgs().CreatorsAsync(default, Ct);
    }

    [Fact]
    public async Task The_list_orders_by_affinity_then_by_number_of_places_then_by_handle_and_rounds_the_percentage()
    {
        var traveler = Guid.NewGuid();
        var store = Substitute.For<IDiscoveryStore>();
        store.GetProfileAsync(traveler, Ct).Returns(new StoredProfile(
            new OnVoyage.Recommendation.Engine.Learning.LearnedProfile(new Dictionary<string, double> { ["history"] = 0.8 }, new HashSet<string>(), new Dictionary<string, double>(), 12, null), Locks.None, "personalized", 1));
        var travelers = Substitute.For<ITravelerReader>();
        travelers.GetAsync(traveler, Ct).Returns(new TravelerInfo(traveler, "fr", "balanced", false, 12, "personalized"));
        var creators = Substitute.For<ICreatorReader>();
        creators.FollowedAsync(traveler, Ct).Returns(new HashSet<Guid>());
        CreatorInfo Make(string handle, int places, params (string Code, double Value)[] vector) =>
            new(Guid.NewGuid(), handle, handle, null, [], vector.ToDictionary(v => v.Code, v => v.Value), places);
        creators.CreatorsAsync("marseille", Ct).Returns([
            Make("zed", 2, ("history", 1d)),
            Make("amy", 2, ("history", 1d)),
            Make("big", 5, ("history", 1d)),
            Make("mix", 9, ("history", 1d), ("nature", 1d)),
            Make("nat", 9, ("nature", 1d)),
        ]);

        var result = await GetCreatorsForMeHandler.Handle(new GetCreatorsForMeQuery(traveler, "marseille", 10), store, travelers, creators, Ct);

        result.Value!.Items.Select(i => i.Handle).ShouldBe(["big", "amy", "zed", "mix", "nat"]);
        result.Value.Items.Select(i => i.Affinity).ShouldBe([100, 100, 100, 71, 0]);
    }
}
