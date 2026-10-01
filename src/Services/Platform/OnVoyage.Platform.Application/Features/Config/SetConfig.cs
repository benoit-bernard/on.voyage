using System.Text.Json;
using System.Text.RegularExpressions;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Features.Config;

public sealed record GetConfigEntryQuery(string Key);

public sealed record SetConfigCommand(string Key, string ValueJson, string Actor);

public static partial class ConfigKey
{
    public const int MaxValueBytes = 64 * 1024;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string key) => Pattern().IsMatch(key);
}

public static class GetConfigEntryHandler
{
    public static async Task<Result<ConfigEntryDto>> Handle(GetConfigEntryQuery query, IRemoteConfigStore store, CancellationToken cancellationToken)
    {
        var entry = await store.FindAsync(query.Key, cancellationToken);
        return entry is null
            ? Result.Failure<ConfigEntryDto>("config_not_found", "Unknown configuration key.")
            : Result.Success(SetConfigHandler.ToDto(entry));
    }
}

public static class SetConfigHandler
{
    public static async Task<Result<ConfigEntryDto>> Handle(SetConfigCommand command, IRemoteConfigStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!ConfigKey.IsValid(command.Key))
        {
            return Result.Failure<ConfigEntryDto>("validation", "Configuration keys are lowercase snake_case, up to 64 characters.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(command.ValueJson) > ConfigKey.MaxValueBytes)
        {
            return Result.Failure<ConfigEntryDto>("validation", "Configuration value is too large.");
        }

        try
        {
            using var _ = JsonDocument.Parse(command.ValueJson);
        }
        catch (JsonException)
        {
            return Result.Failure<ConfigEntryDto>("validation", "Configuration value is not valid JSON.");
        }

        var current = await store.FindAsync(command.Key, cancellationToken);
        var now = clock.GetUtcNow();
        var entry = new RemoteConfigEntry(command.Key, command.ValueJson, (current?.Version ?? 0) + 1, command.Actor, now);
        await store.SaveAsync(entry, new ConfigChangedV1(Guid.CreateVersion7(), now, entry.Key, entry.ValueJson, entry.Version), cancellationToken);
        return Result.Success(ToDto(entry));
    }

    internal static ConfigEntryDto ToDto(RemoteConfigEntry entry) =>
        new(entry.Key, JsonDocument.Parse(entry.ValueJson).RootElement.Clone(), entry.Version, entry.UpdatedBy, entry.UpdatedAt);
}
