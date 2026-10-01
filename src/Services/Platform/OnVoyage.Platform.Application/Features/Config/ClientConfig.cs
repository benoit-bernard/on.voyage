using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Platform.Application.Features.Config;

public enum ConfigScope { Client, Edge }

/// <summary>Remote config + applicable feature flags for a client. <c>Edge</c> returns only <c>security.*</c> for the Gateway.</summary>
public sealed record GetClientConfigQuery(string? Platform, string? AppVersion, Guid? TravelerId, ConfigScope Scope = ConfigScope.Client);

public static class GetClientConfigHandler
{
    // Never sent to apps: abuse-protection tuning and server-side orchestration settings.
    private static readonly HashSet<string> ServerOnly = new(StringComparer.Ordinal) { "security", "deletion" };

    public static async Task<Result<ClientConfigDto>> Handle(
        GetClientConfigQuery query, IRemoteConfigStore config, IFeatureFlagStore flags, CancellationToken cancellationToken)
    {
        var entries = await config.ListAsync(cancellationToken);
        var selected = query.Scope == ConfigScope.Edge
            ? entries.Where(entry => entry.Key == "security")
            : entries.Where(entry => !ServerOnly.Contains(entry.Key));
        var values = selected.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();

        var appVersion = Version.TryParse(query.AppVersion, out var parsed) ? parsed : null;
        var evaluated = query.Scope == ConfigScope.Edge
            ? new Dictionary<string, bool>()
            : (await flags.ListAsync(cancellationToken))
                .OrderBy(flag => flag.Name, StringComparer.Ordinal)
                .ToDictionary(flag => flag.Name, flag => flag.IsEnabledFor(query.Platform, appVersion, query.TravelerId));

        var configuration = values.ToDictionary(entry => entry.Key, entry => JsonDocument.Parse(entry.ValueJson).RootElement.Clone());
        var revisionSource = string.Join('|', values.Select(entry => $"{entry.Key}:{entry.Version}").Concat(evaluated.Select(flag => $"{flag.Key}={flag.Value}")));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionSource)))[..12].ToLowerInvariant();

        return Result.Success(new ClientConfigDto(revision, configuration, evaluated));
    }
}
