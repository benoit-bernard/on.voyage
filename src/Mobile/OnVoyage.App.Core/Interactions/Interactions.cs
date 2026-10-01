using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Core.Interactions;

/// <summary>The Discovery endpoints the app calls. Failures throw <see cref="HttpRequestException"/>, which the outbox treats as "try later".</summary>
public interface IDiscoveryClient
{
    Task<InteractionBatchResponse> PostInteractionsAsync(IReadOnlyList<InteractionDto> batch, CancellationToken cancellationToken);

    Task<IReadOnlyList<OnboardingClipDto>> GetOnboardingClipsAsync(string lang, CancellationToken cancellationToken);

    Task<InteractionBatchResponse> PostOnboardingAsync(OnboardingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Where interactions wait until they reach Discovery (§14.3). The phone apps persist them in <c>user.db</c>; the PWA keeps them in memory
/// and sends at once. A resend is harmless: every interaction carries its <c>client_event_id</c>.
/// </summary>
public interface IInteractionOutbox
{
    Task EnqueueAsync(InteractionDto interaction, CancellationToken cancellationToken);

    /// <summary>Tries to send what waits now (network back, app closing). Never throws on a network failure.</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}
