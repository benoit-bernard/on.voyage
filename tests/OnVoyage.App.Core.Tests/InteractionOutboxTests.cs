using System.Net;
using NSubstitute;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Core.Tests;

public sealed class InteractionOutboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static InteractionDto Like() => new(Guid.NewGuid(), "like", Guid.NewGuid(), Now);

    private static InteractionBatchResponse Answer(double history = 0.4) =>
        new(new Dictionary<string, double> { ["history"] = history }, 7, 1, 1, 0, [Guid.Parse("00000000-0000-0000-0000-0000000000aa")]);

    [Fact]
    public async Task The_servers_vector_replaces_the_local_one()
    {
        var client = Substitute.For<IDiscoveryClient>();
        client.PostInteractionsAsync(Arg.Any<IReadOnlyList<InteractionDto>>(), Arg.Any<CancellationToken>()).Returns(Answer());
        var profiles = new InMemoryProfileStore();
        await profiles.SaveAsync(new LocalProfile { Affinities = new() { ["nature"] = 0.9 }, Depth = 2 }, Ct);

        (await new InteractionSender(client, profiles).SendAsync([Like()], Ct)).ShouldBeTrue();

        var profile = await profiles.LoadAsync(Ct);
        profile.Affinities.ShouldBe(new Dictionary<string, double> { ["history"] = 0.4 });
        profile.Depth.ShouldBe(7);
        profile.Excluded.ShouldContain(Guid.Parse("00000000-0000-0000-0000-0000000000aa"));
    }

    [Fact]
    public async Task A_network_failure_keeps_the_interactions_and_the_next_call_sends_them_all_with_the_same_ids()
    {
        var client = Substitute.For<IDiscoveryClient>();
        var sent = new List<IReadOnlyList<InteractionDto>>();
        var fail = true;
        client.PostInteractionsAsync(Arg.Any<IReadOnlyList<InteractionDto>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            sent.Add(call.Arg<IReadOnlyList<InteractionDto>>());
            return fail ? throw new HttpRequestException("offline") : Task.FromResult(Answer());
        });
        var outbox = new DirectInteractionOutbox(new InteractionSender(client, new InMemoryProfileStore()));
        var first = Like();

        await outbox.EnqueueAsync(first, Ct);
        fail = false;
        var second = Like();
        await outbox.EnqueueAsync(second, Ct);

        sent.Count.ShouldBe(2);
        sent[1].Select(i => i.ClientEventId).ShouldBe([first.ClientEventId, second.ClientEventId]);
        await outbox.FlushAsync(Ct);
        sent.Count.ShouldBe(2); // nothing is left to send
    }

    [Fact]
    public async Task A_batch_the_server_refuses_for_good_is_dropped_so_it_does_not_block_the_rest()
    {
        var client = Substitute.For<IDiscoveryClient>();
        var calls = 0;
        client.PostInteractionsAsync(Arg.Any<IReadOnlyList<InteractionDto>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return Task.FromException<InteractionBatchResponse>(new HttpRequestException("bad", null, HttpStatusCode.BadRequest));
        });
        var outbox = new DirectInteractionOutbox(new InteractionSender(client, new InMemoryProfileStore()));

        await outbox.EnqueueAsync(Like(), Ct);
        await outbox.FlushAsync(Ct);

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_server_error_or_an_expired_session_is_retried_not_dropped()
    {
        var client = Substitute.For<IDiscoveryClient>();
        var calls = 0;
        client.PostInteractionsAsync(Arg.Any<IReadOnlyList<InteractionDto>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return Task.FromException<InteractionBatchResponse>(new HttpRequestException("x", null, calls == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.Unauthorized));
        });
        var outbox = new DirectInteractionOutbox(new InteractionSender(client, new InMemoryProfileStore()));

        await outbox.EnqueueAsync(Like(), Ct);
        await outbox.FlushAsync(Ct);

        calls.ShouldBe(2);
    }
}
