using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.LocalData;
using OnVoyage.App.LocalData.Sync;

namespace OnVoyage.App.LocalData.Tests;

public sealed class SyncQueueTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"user-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeTransport _transport = new();

    private sealed class FakeTransport : ISyncTransport
    {
        public List<IReadOnlyList<SyncEvent>> Sent { get; } = [];

        public SyncOutcome Next { get; set; } = SyncOutcome.Accepted;

        public Task<SyncOutcome> SendAsync(IReadOnlyList<SyncEvent> batch, CancellationToken cancellationToken)
        {
            Sent.Add(batch);
            return Task.FromResult(Next);
        }
    }

    private sealed class Factory(string path) : IDbContextFactory<UserDbContext>
    {
        public UserDbContext CreateDbContext() => new(new DbContextOptionsBuilder<UserDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private async Task<SyncOutbox> OpenAsync()
    {
        var factory = new Factory(_path);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(Ct);
        }

        return new SyncOutbox(factory, _transport, _time);
    }

    [Fact]
    public async Task Waits_for_twenty_items_or_sixty_seconds()
    {
        var queue = await OpenAsync();
        for (var i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync("event", "{}", cancellationToken: Ct);
        }

        (await queue.FlushAsync(cancellationToken: Ct)).ShouldBe(0);
        _transport.Sent.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(60));
        (await queue.FlushAsync(cancellationToken: Ct)).ShouldBe(5);
        (await queue.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Sends_full_batches_of_twenty()
    {
        var queue = await OpenAsync();
        for (var i = 0; i < 45; i++)
        {
            await queue.EnqueueAsync("event", "{}", cancellationToken: Ct);
        }

        // Two full batches go out at once; the five left wait for the delay.
        (await queue.FlushAsync(cancellationToken: Ct)).ShouldBe(40);
        _transport.Sent.Select(b => b.Count).ShouldBe([20, 20]);
        (await queue.CountAsync(Ct)).ShouldBe(5);
    }

    [Fact]
    public async Task A_forced_flush_sends_a_short_batch_now()
    {
        var queue = await OpenAsync();
        await queue.EnqueueAsync("visit", "{}", cancellationToken: Ct);
        (await queue.FlushAsync(force: true, cancellationToken: Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Failures_back_off_exponentially_and_the_items_are_kept()
    {
        var queue = await OpenAsync();
        await queue.EnqueueAsync("visit", "{}", cancellationToken: Ct);
        _transport.Next = SyncOutcome.Retry;

        await queue.FlushAsync(force: true, cancellationToken: Ct);
        _transport.Sent.Count.ShouldBe(1);

        // Inside the 5 s delay nothing is tried.
        _time.Advance(TimeSpan.FromSeconds(4));
        await queue.FlushAsync(force: true, cancellationToken: Ct);
        _transport.Sent.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(2));
        await queue.FlushAsync(force: true, cancellationToken: Ct);
        _transport.Sent.Count.ShouldBe(2);

        // The second failure doubles the wait to 10 s.
        _time.Advance(TimeSpan.FromSeconds(9));
        await queue.FlushAsync(force: true, cancellationToken: Ct);
        _transport.Sent.Count.ShouldBe(2);
        _time.Advance(TimeSpan.FromSeconds(2));
        _transport.Next = SyncOutcome.Accepted;
        (await queue.FlushAsync(force: true, cancellationToken: Ct)).ShouldBe(1);
        (await queue.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_delay_is_capped_at_one_hour()
    {
        var queue = await OpenAsync();
        queue.Backoff(1).ShouldBe(TimeSpan.FromSeconds(5));
        queue.Backoff(3).ShouldBe(TimeSpan.FromSeconds(20));
        queue.Backoff(40).ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Resumes_after_a_restart_with_the_same_events()
    {
        var first = await OpenAsync();
        var id = await first.EnqueueAsync("visit", """{"poi_id":"x"}""", cancellationToken: Ct);
        _transport.Next = SyncOutcome.Retry;
        await first.FlushAsync(force: true, cancellationToken: Ct);

        // A new queue over the same file is what the app has after being killed and reopened.
        var second = new SyncOutbox(new Factory(_path), _transport, _time);
        (await second.CountAsync(Ct)).ShouldBe(1);
        _time.Advance(TimeSpan.FromMinutes(1));
        _transport.Next = SyncOutcome.Accepted;
        (await second.FlushAsync(cancellationToken: Ct)).ShouldBe(1);

        var resent = _transport.Sent[^1].Single();
        resent.ClientEventId.ShouldBe(id);
        resent.Payload.ShouldBe("""{"poi_id":"x"}""");
    }

    [Fact]
    public async Task A_repeated_client_event_id_is_queued_once()
    {
        var queue = await OpenAsync();
        var id = Guid.NewGuid();
        await queue.EnqueueAsync("interaction", "{}", id, Ct);
        await queue.EnqueueAsync("interaction", "{}", id, Ct);
        (await queue.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_batch_lost_on_the_wire_is_sent_again_with_the_same_ids()
    {
        var queue = await OpenAsync();
        await queue.EnqueueAsync("a", "{}", cancellationToken: Ct);
        await queue.EnqueueAsync("b", "{}", cancellationToken: Ct);
        _transport.Next = SyncOutcome.Retry;
        await queue.FlushAsync(force: true, cancellationToken: Ct);
        _time.Advance(TimeSpan.FromSeconds(6));
        _transport.Next = SyncOutcome.Accepted;
        await queue.FlushAsync(force: true, cancellationToken: Ct);

        _transport.Sent[0].Select(e => e.ClientEventId).ShouldBe(_transport.Sent[1].Select(e => e.ClientEventId));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
