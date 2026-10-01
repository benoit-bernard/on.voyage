using OnVoyage.Insights.Contracts;

namespace OnVoyage.App.Core.Analytics;

/// <summary>The traveler's answer to « Nous aider à améliorer ON.VOYAGE (statistiques d'usage) » (§16.3).</summary>
public enum AnalyticsConsentState
{
    /// <summary>No choice yet. This counts as a refusal: nothing but essential events leaves the device.</summary>
    Undecided,

    Granted,

    Refused,
}

/// <summary>
/// The statistics consent as the app knows it. The settings screen (T-618) sets it after Platform has recorded the choice; the queue reacts to
/// the change at once. Platform holds the truth (<c>platform.consent</c>); this is its local copy.
/// </summary>
public sealed class AnalyticsConsent
{
    public AnalyticsConsentState State { get; private set; }

    public event Action<AnalyticsConsentState>? Changed;

    public void Set(AnalyticsConsentState state)
    {
        if (state == State)
        {
            return;
        }

        State = state;
        Changed?.Invoke(state);
    }
}

/// <summary>What every event carries besides its own properties. Neither is an identifier of the device or of the traveler.</summary>
public sealed record AnalyticsContext(string AppVersion, string Platform)
{
    public static AnalyticsContext Detect(string appVersion) => new(
        appVersion,
        OperatingSystem.IsAndroid() ? Platforms.Android : OperatingSystem.IsIOS() ? Platforms.Ios : Platforms.Web);
}

public enum AnalyticsSendOutcome
{
    /// <summary>The server took the batch.</summary>
    Sent,

    /// <summary>Offline, timeout, throttling, server error: keep the events and try again later.</summary>
    Retry,

    /// <summary>The server will never accept this batch (validation): drop it so it does not block the next ones.</summary>
    Rejected,
}

/// <summary>Sends a batch to Insights (<c>POST /api/insights/v1/events</c>). Never throws for network or HTTP faults.</summary>
public interface IAnalyticsTransport
{
    Task<AnalyticsSendOutcome> SendAsync(IReadOnlyList<EventDto> batch, CancellationToken cancellationToken);
}

/// <summary>Used until a host registers a real transport: the events go nowhere.</summary>
public sealed class NullAnalyticsTransport : IAnalyticsTransport
{
    public Task<AnalyticsSendOutcome> SendAsync(IReadOnlyList<EventDto> batch, CancellationToken cancellationToken) => Task.FromResult(AnalyticsSendOutcome.Sent);
}

/// <summary>
/// Keeps the events that wait for the network across restarts (the host backs it with <c>user.db</c>). Only events allowed to leave the device
/// are saved: the ones held for a consent that is still undecided stay in memory.
/// </summary>
public interface IAnalyticsStore
{
    Task<IReadOnlyList<EventDto>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Replaces what is saved by <paramref name="pending"/>.</summary>
    Task SaveAsync(IReadOnlyList<EventDto> pending, CancellationToken cancellationToken);
}

public sealed class InMemoryAnalyticsStore : IAnalyticsStore
{
    private IReadOnlyList<EventDto> _saved = [];

    public Task<IReadOnlyList<EventDto>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_saved);

    public Task SaveAsync(IReadOnlyList<EventDto> pending, CancellationToken cancellationToken)
    {
        _saved = pending;
        return Task.CompletedTask;
    }
}

public sealed record AnalyticsOptions
{
    /// <summary>Events per request (§14.8).</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>How long the first waiting event waits for company before the batch is sent anyway.</summary>
    public TimeSpan FlushAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Longest wait between two attempts while offline.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Events kept while the network is down; the oldest go first.</summary>
    public int MaxQueued { get; init; } = 1000;

    /// <summary>Non-essential events kept in memory while the consent is undecided (the onboarding).</summary>
    public int MaxHeld { get; init; } = 200;

    /// <summary>A pause longer than this starts a new session.</summary>
    public TimeSpan SessionGap { get; init; } = TimeSpan.FromMinutes(30);
}
