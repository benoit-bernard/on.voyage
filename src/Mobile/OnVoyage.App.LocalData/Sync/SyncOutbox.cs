using Microsoft.EntityFrameworkCore;
using OnVoyage.App.Core.Interactions;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.LocalData.Sync;

public static class SyncTypes
{
    public const string Interaction = "interaction";
}

public sealed record SyncEvent(Guid ClientEventId, string Type, string Payload, DateTimeOffset CreatedAt);

public enum SyncOutcome
{
    /// <summary>The server took every event of the batch (or already had them: the send is idempotent).</summary>
    Accepted,

    /// <summary>Network or server trouble: keep the batch and try again later.</summary>
    Retry,
}

/// <summary>Sends a batch to the platform. Events carry their <see cref="SyncEvent.ClientEventId"/> so a repeated send is harmless.</summary>
public interface ISyncTransport
{
    Task<SyncOutcome> SendAsync(IReadOnlyList<SyncEvent> batch, CancellationToken cancellationToken);
}

/// <summary>
/// Used until the platform exposes the events endpoint (Insights, T-801): every send is deferred, so events stay in <c>user.db</c>
/// and nothing is lost or sent. Replace it by registering a real <see cref="ISyncTransport"/> before <c>AddLocalData</c>.
/// </summary>
/// <summary>Sends the queued interactions to Discovery. The answer's vector replaces the local profile (the server is the reference).</summary>
public sealed class DiscoverySyncTransport(InteractionSender sender) : ISyncTransport
{
    public async Task<SyncOutcome> SendAsync(IReadOnlyList<SyncEvent> batch, CancellationToken cancellationToken)
    {
        var interactions = batch
            .Where(e => e.Type == SyncTypes.Interaction)
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<InteractionDto>(e.Payload))
            .OfType<InteractionDto>()
            .ToArray();
        if (interactions.Length == 0)
        {
            return SyncOutcome.Accepted;
        }

        return await sender.SendAsync(interactions, cancellationToken) ? SyncOutcome.Accepted : SyncOutcome.Retry;
    }
}

public sealed class HoldingSyncTransport : ISyncTransport
{
    public Task<SyncOutcome> SendAsync(IReadOnlyList<SyncEvent> batch, CancellationToken cancellationToken) => Task.FromResult(SyncOutcome.Retry);
}

public sealed class SyncSettings
{
    public int BatchSize { get; init; } = 20;

    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetry { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The persistent queue of §14.3. Items live in <c>user.db</c>, so a restart resumes where it stopped. A batch goes out when 20 items are
/// waiting or the oldest has waited 60 s; after a failure the batch's items wait 5 s, 10 s, 20 s… up to an hour. Only the app's own
/// timer or a network-restored signal calls <see cref="FlushAsync"/>.
/// </summary>
public sealed class SyncOutbox(IDbContextFactory<UserDbContext> factory, ISyncTransport transport, TimeProvider time, SyncSettings? settings = null) : IDisposable
{
    private readonly SyncSettings _settings = settings ?? new SyncSettings();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Queues an event. A repeated <paramref name="clientEventId"/> is ignored, so a retry of the caller never doubles an event.</summary>
    public async Task<Guid> EnqueueAsync(string type, string payload, Guid? clientEventId = null, CancellationToken cancellationToken = default)
    {
        var id = clientEventId ?? Guid.NewGuid();
        var now = time.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.SyncItems.AnyAsync(i => i.ClientEventId == id, cancellationToken))
        {
            db.SyncItems.Add(new SyncItem { ClientEventId = id, Type = type, Payload = payload, CreatedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }

        return id;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.SyncItems.CountAsync(cancellationToken);
    }

    /// <summary>Sends what is due. <paramref name="force"/> (network back, app closing) ignores the 20-item / 60-s rule but still respects back-off.</summary>
    /// <returns>How many events the server accepted.</returns>
    public async Task<int> FlushAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sent = 0;
            while (true)
            {
                var now = time.GetUtcNow();
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                var ticks = await db.SyncItems.OrderBy(i => i.Id).ToListAsync(cancellationToken);
                var due = ticks.Where(i => i.NextAttemptAt <= now).Take(_settings.BatchSize).ToList();
                if (due.Count == 0)
                {
                    return sent;
                }

                var full = due.Count >= _settings.BatchSize;
                var waited = now - due.Min(i => i.CreatedAt) >= _settings.MaxWait;
                var retrying = due.Any(i => i.Attempts > 0);
                if (!force && !full && !waited && !retrying)
                {
                    return sent;
                }

                var batch = due.Select(i => new SyncEvent(i.ClientEventId, i.Type, i.Payload, i.CreatedAt)).ToList();
                SyncOutcome outcome;
                try
                {
                    outcome = await transport.SendAsync(batch, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    outcome = SyncOutcome.Retry;
                }

                if (outcome == SyncOutcome.Accepted)
                {
                    db.SyncItems.RemoveRange(due);
                    await db.SaveChangesAsync(cancellationToken);
                    sent += due.Count;
                    continue;
                }

                foreach (var item in due)
                {
                    item.Attempts++;
                    item.NextAttemptAt = now + Backoff(item.Attempts);
                }

                await db.SaveChangesAsync(cancellationToken);
                return sent;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    internal TimeSpan Backoff(int attempts)
    {
        var seconds = _settings.FirstRetry.TotalSeconds * Math.Pow(2, Math.Min(attempts - 1, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, _settings.MaxRetry.TotalSeconds));
    }
}

/// <summary>The 60-second beat of §14.3: sends what is due. The host also calls <see cref="SyncOutbox.FlushAsync"/> when the network comes back.</summary>
public sealed class SyncScheduler(SyncOutbox outbox, TimeProvider time) : IDisposable
{
    private ITimer? _timer;

    public void Start(TimeSpan? every = null)
    {
        var period = every ?? TimeSpan.FromSeconds(60);
        _timer ??= time.CreateTimer(_ => _ = TickAsync(), null, period, period);
    }

    public void Dispose() => _timer?.Dispose();

    internal async Task TickAsync()
    {
        try
        {
            await outbox.FlushAsync(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // The next beat tries again; a failing disk must not take the app down.
        }
    }
}
