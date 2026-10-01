using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Features.Flags;

public sealed record ListFlagsQuery;

public sealed record SetFlagCommand(string Name, bool Enabled, int RolloutPercent, IReadOnlyList<string> Platforms, string? MinAppVersion);

public static class FlagHandler
{
    private static readonly string[] KnownPlatforms = ["android", "ios", "web"];

    public static async Task<Result<IReadOnlyList<FeatureFlagDto>>> Handle(ListFlagsQuery query, IFeatureFlagStore store, CancellationToken cancellationToken)
    {
        var flags = await store.ListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<FeatureFlagDto>>([.. flags.OrderBy(flag => flag.Name, StringComparer.Ordinal).Select(ToDto)]);
    }

    public static async Task<Result<FeatureFlagDto>> Handle(SetFlagCommand command, IFeatureFlagStore store, CancellationToken cancellationToken)
    {
        var known = KnownFeatureFlags.Defaults.Any(flag => flag.Name == command.Name) || (await store.ListAsync(cancellationToken)).Any(flag => flag.Name == command.Name);
        if (!known)
        {
            return Result.Failure<FeatureFlagDto>("flag_not_found", "Unknown feature flag.");
        }

        if (command.RolloutPercent is < 0 or > 100)
        {
            return Result.Failure<FeatureFlagDto>("validation", "Rollout percent must be between 0 and 100.");
        }

        if (command.Platforms.Any(platform => !KnownPlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase)))
        {
            return Result.Failure<FeatureFlagDto>("validation", "Platforms must be android, ios or web.");
        }

        if (command.MinAppVersion is not null && !Version.TryParse(command.MinAppVersion, out _))
        {
            return Result.Failure<FeatureFlagDto>("validation", "Minimum app version is not a valid version.");
        }

        var flag = new FeatureFlag(command.Name, command.Enabled, command.RolloutPercent, [.. command.Platforms.Select(platform => platform.ToLowerInvariant())], command.MinAppVersion);
        await store.SaveAsync(flag, cancellationToken);
        return Result.Success(ToDto(flag));
    }

    private static FeatureFlagDto ToDto(FeatureFlag flag) => new(flag.Name, flag.Enabled, flag.RolloutPercent, flag.Platforms, flag.MinAppVersion);
}
