using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Catalog.Application.IntegrationEvents;

/// <summary>Keeps the local config snapshot in step with Platform. Redelivery and reordering are harmless: only newer versions apply.</summary>
public static class ConfigChangedHandler
{
    public static Task Handle(ConfigChangedV1 changed, IConfigSnapshotStore store, CancellationToken cancellationToken) =>
        store.ApplyAsync(changed.Key, changed.ValueJson, changed.Version, cancellationToken);
}
