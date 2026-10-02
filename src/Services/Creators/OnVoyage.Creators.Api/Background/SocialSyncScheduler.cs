using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.Ports;
using Wolverine;

namespace OnVoyage.Creators.Api.Background;

/// <summary>
/// The daily incremental synchronisation of connected accounts (F-27): every quarter of an hour it asks for the accounts that were last synchronised
/// more than <c>Creators:Social:SyncIntervalHours</c> (24) ago and sends one <see cref="SyncConnectedAccountCommand"/> for each. It runs inside the
/// Creators API process for now (there is no separate Creators worker yet) and only when <c>Creators:Social:SyncEnabled</c> is true. An account
/// whose tokens are dead is not active any more and is not asked for again until the creator connects it again.
/// </summary>
internal sealed class SocialSyncScheduler(IServiceScopeFactory scopes, IMessageBus bus, TimeProvider clock, IConfiguration configuration, ILogger<SocialSyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);
    private readonly Dictionary<Guid, DateTimeOffset> _asked = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The synchronisation of connected accounts could not be scheduled.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var interval = TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Creators:Social:SyncIntervalHours", 24)));
        await using var scope = scopes.CreateAsyncScope();
        var due = await scope.ServiceProvider.GetRequiredService<IConnectedAccountRepository>().ListDueAsync(now - interval, 50, cancellationToken);

        // An account asked for recently is still being served (or was refused): do not stack requests behind it.
        foreach (var old in _asked.Where(item => now - item.Value > TimeSpan.FromHours(1)).Select(item => item.Key).ToList())
        {
            _asked.Remove(old);
        }

        var sent = 0;
        foreach (var id in due.Where(id => !_asked.ContainsKey(id)))
        {
            _asked[id] = now;
            await bus.PublishAsync(new SyncConnectedAccountCommand(id));
            sent++;
        }

        return sent;
    }
}
