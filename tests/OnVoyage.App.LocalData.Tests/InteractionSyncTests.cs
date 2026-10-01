using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Wishes;
using OnVoyage.App.LocalData.Sync;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.LocalData.Tests;

public sealed class InteractionSyncTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"user-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly IDiscoveryClient _client = Substitute.For<IDiscoveryClient>();
    private readonly InMemoryProfileStore _profiles = new();
    private readonly ServiceProvider _services;

    public InteractionSyncTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(_client);
        services.AddSingleton<IProfileStore>(_profiles);
        services.AddAppCore();
        services.AddLocalData(_path);
        _services = services.BuildServiceProvider();
    }

    private static InteractionDto Like() => new(Guid.NewGuid(), "like", Guid.NewGuid(), new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Interactions_wait_on_disk_and_leave_with_the_next_flush_in_one_batch()
    {
        await _services.InitializeLocalDataAsync(Ct);
        var batches = new List<IReadOnlyList<InteractionDto>>();
        _client.PostInteractionsAsync(Arg.Any<IReadOnlyList<InteractionDto>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            batches.Add(call.Arg<IReadOnlyList<InteractionDto>>());
            return new InteractionBatchResponse(new Dictionary<string, double> { ["history"] = 0.3 }, 3, 1, 1, 0, []);
        });
        var outbox = _services.GetRequiredService<IInteractionOutbox>();

        var first = Like();
        var second = Like();
        await outbox.EnqueueAsync(first, Ct);
        await outbox.EnqueueAsync(second, Ct);
        batches.ShouldBeEmpty(); // under 20 items and under 60 s: it waits

        await outbox.FlushAsync(Ct);

        batches.ShouldHaveSingleItem().Select(i => i.ClientEventId).ShouldBe([first.ClientEventId, second.ClientEventId]);
        (await _profiles.LoadAsync(Ct)).Affinities["history"].ShouldBe(0.3);
        (await _services.GetRequiredService<SyncOutbox>().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task When_the_server_is_unreachable_nothing_is_lost_and_the_retry_resends_the_same_ids()
    {
        await _services.InitializeLocalDataAsync(Ct);
        var seen = new List<Guid>();
        var fail = true;
        _client.PostInteractionsAsync(Arg.Any<IReadOnlyList<InteractionDto>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen.AddRange(call.Arg<IReadOnlyList<InteractionDto>>().Select(i => i.ClientEventId));
            return fail ? throw new HttpRequestException("offline") : Task.FromResult(new InteractionBatchResponse(new Dictionary<string, double>(), 0, 1, 1, 0, []));
        });
        var outbox = _services.GetRequiredService<IInteractionOutbox>();
        var like = Like();
        await outbox.EnqueueAsync(like, Ct);

        await outbox.FlushAsync(Ct);
        (await _services.GetRequiredService<SyncOutbox>().CountAsync(Ct)).ShouldBe(1);

        fail = false;
        _time.Advance(TimeSpan.FromSeconds(10));
        await outbox.FlushAsync(Ct);

        seen.ShouldBe([like.ClientEventId, like.ClientEventId]);
        (await _services.GetRequiredService<SyncOutbox>().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Reminder_dates_persist_in_user_db_and_are_never_queued_for_sync()
    {
        await _services.InitializeLocalDataAsync(Ct);
        var store = _services.GetRequiredService<IReminderStore>();
        var poi = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 5, 20, 9, 30, 0, TimeSpan.Zero);
        await store.MarkAsync(poi, at, Ct);
        await store.MarkAsync(poi, at.AddDays(2), Ct);

        (await store.LoadAsync(Ct))[poi].ShouldBe(at.AddDays(2));
        (await _services.GetRequiredService<SyncOutbox>().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_scheduler_beat_sends_what_has_waited_a_minute()
    {
        await _services.InitializeLocalDataAsync(Ct);
        var calls = 0;
        _client.PostInteractionsAsync(Arg.Any<IReadOnlyList<InteractionDto>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return new InteractionBatchResponse(new Dictionary<string, double>(), 0, 1, 1, 0, []);
        });
        await _services.GetRequiredService<IInteractionOutbox>().EnqueueAsync(Like(), Ct);
        var scheduler = _services.GetRequiredService<SyncScheduler>();
        scheduler.Start();

        _time.Advance(TimeSpan.FromSeconds(60));
        await scheduler.TickAsync();

        calls.ShouldBe(1);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
