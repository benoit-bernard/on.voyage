using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Ports;

public interface IRemoteConfigStore
{
    Task<IReadOnlyList<RemoteConfigEntry>> ListAsync(CancellationToken cancellationToken);

    Task<RemoteConfigEntry?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>Persists the new version, its history row and the integration event in one transaction (outbox).</summary>
    Task SaveAsync(RemoteConfigEntry entry, ConfigChangedV1 changed, CancellationToken cancellationToken);
}

public interface IFeatureFlagStore
{
    Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(FeatureFlag flag, CancellationToken cancellationToken);
}

public interface IConsentStore
{
    Task<IReadOnlyList<Consent>> ListAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Persists the consent and the integration event in one transaction (outbox).</summary>
    Task SaveAsync(Consent consent, ConsentChangedV1 changed, CancellationToken cancellationToken);
}
