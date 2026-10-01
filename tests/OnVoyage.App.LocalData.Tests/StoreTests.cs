using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.LocalData.Sync;

namespace OnVoyage.App.LocalData.Tests;

public sealed class StoreTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"user-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    private sealed class NullTransport : ISyncTransport
    {
        public Task<SyncOutcome> SendAsync(IReadOnlyList<SyncEvent> batch, CancellationToken cancellationToken) => Task.FromResult(SyncOutcome.Accepted);
    }

    public StoreTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISyncTransport, NullTransport>();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)));
        services.AddAppCore();
        services.AddLocalData(_path);
        _services = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Tell_dates_survive_a_new_container_over_the_same_file()
    {
        await _services.InitializeLocalDataAsync(Ct);
        var poi = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 5, 20, 9, 30, 0, TimeSpan.Zero);
        await _services.GetRequiredService<ITellHistoryStore>().MarkAsync(poi, at, Ct);
        await _services.GetRequiredService<ITellHistoryStore>().MarkAsync(poi, at.AddDays(1), Ct);

        var loaded = await _services.GetRequiredService<ITellHistoryStore>().LoadAsync(Ct);
        loaded[poi].ShouldBe(at.AddDays(1));
    }

    [Fact]
    public async Task A_visit_is_stored_and_queued_without_coordinates()
    {
        await _services.InitializeLocalDataAsync(Ct);
        var poi = Guid.NewGuid();
        await _services.GetRequiredService<IVisitSink>().RecordAsync(new Visit(poi, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(7), 0.7), Ct);

        await using var db = await _services.GetRequiredService<IDbContextFactory<UserDbContext>>().CreateDbContextAsync(Ct);
        (await db.Visits.SingleAsync(Ct)).DwellSeconds.ShouldBe(420);
        var queued = await db.SyncItems.SingleAsync(Ct);
        queued.Type.ShouldBe("visit");
        queued.Payload.ShouldContain(poi.ToString());
        queued.Payload.ShouldNotContain("lat");
        queued.Payload.ShouldNotContain("lng");
    }

    [Fact]
    public async Task Flags_default_to_false_and_persist()
    {
        await _services.InitializeLocalDataAsync(Ct);
        var flags = _services.GetRequiredService<IFlagStore>();
        (await flags.GetAsync("ai_voice_notice", Ct)).ShouldBeFalse();
        await flags.SetAsync("ai_voice_notice", true, Ct);
        (await flags.GetAsync("ai_voice_notice", Ct)).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
