using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Creators;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;
using OnVoyage.Creators.Contracts;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Core.Tests;

public sealed class CreatorsServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Poi = Guid.CreateVersion7();

    private sealed class Outbox : IInteractionOutbox
    {
        public List<InteractionDto> Items { get; } = [];

        public Task EnqueueAsync(InteractionDto interaction, CancellationToken cancellationToken)
        {
            Items.Add(interaction);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingSink : IAnalyticsSink
    {
        public List<(string Name, IReadOnlyDictionary<string, object?> Properties)> Events { get; } = [];

        public void Track(string name, IReadOnlyDictionary<string, object?> properties) => Events.Add((name, properties));
    }

    private static (CreatorsService Service, ICreatorsClient Client, Outbox Outbox, RecordingSink Sink) Build()
    {
        var client = Substitute.For<ICreatorsClient>();
        var outbox = new Outbox();
        var sink = new RecordingSink();
        var recorder = new InteractionRecorder(new InMemoryProfileStore(), outbox, new FakeTimeProvider());
        return (new CreatorsService(client, recorder, new MediaLocator("https://media.test/media/"), sink), client, outbox, sink);
    }

    private static PoiCreatorItemDto Item(string handle, string? tip = null) =>
        new(new CreatorSummaryDto(Guid.NewGuid(), handle, handle, "a.jpg", [], 1), tip, null);

    [Theory]
    [InlineData(92, "1:32")]
    [InlineData(5, "0:05")]
    [InlineData(3600, "60:00")]
    [InlineData(0, "")]
    [InlineData(null, "")]
    public void Durations_read_minutes_and_seconds(int? seconds, string expected) => CreatorLabels.Duration(seconds).ShouldBe(expected);

    [Fact]
    public void A_page_without_a_follower_count_says_new_creator()
    {
        CreatorLabels.Followers(Page(null)).ShouldBe("Nouveau créateur");
        CreatorLabels.Followers(Page(1)).ShouldBe("1 abonné");
        CreatorLabels.Followers(Page(25)).ShouldBe("25 abonnés");
    }

    private static CreatorPageDto Page(int? followers) => new(Guid.NewGuid(), "h", "H", null, null, [], [], [], followers, followers is null, 0, 0, false, [], []);

    [Fact]
    public async Task Media_paths_become_absolute_addresses_and_absolute_ones_are_kept()
    {
        var (service, client, _, _) = Build();
        client.GetPoiCreatorsAsync(Poi, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PoiCreatorsDto(Poi, 1, [Item("anna")]));

        var block = await service.LoadBlockAsync(Poi, "marseille", all: false, Ct);

        block.Cards.Single().AvatarUrl.ShouldBe("https://media.test/media/a.jpg");
        new MediaLocator("/media").Resolve("https://cdn.test/x.jpg").ShouldBe("https://cdn.test/x.jpg");
        new MediaLocator("/media").Resolve(null).ShouldBeNull();
    }

    [Fact]
    public async Task The_order_is_the_affinity_then_the_handle_and_creators_unknown_to_discovery_come_last()
    {
        var (service, client, _, _) = Build();
        var items = new[] { Item("zoe"), Item("amy"), Item("bob"), Item("kim") };
        client.GetPoiCreatorsAsync(Poi, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PoiCreatorsDto(Poi, 4, items));
        client.GetCreatorsForMeAsync("marseille", Arg.Any<CancellationToken>()).Returns(new CreatorsForMeDto(
            "marseille",
            [
                new CreatorForMeDto(items[0].Creator.Id, "zoe", "zoe", null, [], 1, 80, false),
                new CreatorForMeDto(items[2].Creator.Id, "bob", "bob", null, [], 1, 80, false),
            ],
            "personalized"));

        var block = await service.LoadBlockAsync(Poi, "marseille", all: true, Ct);

        block.Cards.Select(c => c.Handle).ShouldBe(["bob", "zoe", "amy", "kim"]);
    }

    [Fact]
    public async Task Following_reports_the_event_without_anything_but_the_creator_and_a_withdrawn_creator_gives_null()
    {
        var (service, client, _, sink) = Build();
        var creator = Guid.NewGuid();
        client.SetFollowAsync(creator, true, Arg.Any<CancellationToken>()).Returns(new FollowStateDto(creator, true));
        client.SetFollowAsync(creator, false, Arg.Any<CancellationToken>()).Returns(new FollowStateDto(creator, false));
        client.SetFollowAsync(Arg.Is<Guid>(id => id != creator), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((FollowStateDto?)null);

        (await service.SetFollowAsync(creator, true, Ct)).ShouldBe(true);
        (await service.SetFollowAsync(creator, false, Ct)).ShouldBe(false);
        (await service.SetFollowAsync(Guid.NewGuid(), true, Ct)).ShouldBeNull();

        sink.Events.Select(e => e.Name).ShouldBe(["creator_followed", "creator_unfollowed"]);
        sink.Events.ShouldAllBe(e => e.Properties.Count == 1 && e.Properties.ContainsKey("creator_id"));
    }

    [Fact]
    public async Task Opening_a_content_teaches_the_place_vector_a_little_and_is_sent_once()
    {
        var (service, client, outbox, sink) = Build();
        client.GetPoiCreatorsAsync(Poi, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PoiCreatorsDto(Poi, 1, [Item("anna")]));
        var card = (await service.LoadBlockAsync(Poi, "marseille", false, Ct)).Cards.Single();

        await service.OpenedContentAsync(card, Poi, new Dictionary<string, double> { ["history"] = 1d }, Ct);

        outbox.Items.ShouldHaveSingleItem().Kind.ShouldBe("creator_content_opened");
        sink.Events.ShouldHaveSingleItem().Name.ShouldBe("creator_content_opened");
    }
}
