using System.Security.Cryptography;
using System.Text;

namespace OnVoyage.Platform.Domain;

public sealed record RemoteConfigEntry(string Key, string ValueJson, int Version, string UpdatedBy, DateTimeOffset UpdatedAt);

public static class ConsentKinds
{
    public const string Analytics = "analytics";
    public const string AdsPersonalization = "ads_personalization";

    public static IReadOnlyList<string> All { get; } = [Analytics, AdsPersonalization];
}

public sealed record Consent(Guid TravelerId, string Kind, bool Granted, string TextVersion, DateTimeOffset UpdatedAt);

/// <summary>Feature flag with percentage rollout, platform and minimum app version (§18).</summary>
public sealed record FeatureFlag(string Name, bool Enabled, int RolloutPercent, IReadOnlyList<string> Platforms, string? MinAppVersion)
{
    /// <summary>Stable bucket in [0, 100) for a traveler and a flag; the same traveler always lands in the same bucket.</summary>
    public static int Bucket(string flagName, Guid travelerId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{flagName}:{travelerId:D}"));
        return (int)(BitConverter.ToUInt32(digest, 0) % 100u);
    }

    public bool IsEnabledFor(string? platform, Version? appVersion, Guid? travelerId)
    {
        if (!Enabled)
        {
            return false;
        }

        if (Platforms.Count > 0 && !Platforms.Contains(platform ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (MinAppVersion is not null && Version.TryParse(MinAppVersion, out var minimum) && (appVersion is null || appVersion < minimum))
        {
            return false;
        }

        return RolloutPercent switch
        {
            >= 100 => true,
            <= 0 => false,
            // A partial rollout needs a stable identity; without one the flag stays off.
            _ => travelerId is { } id && Bucket(Name, id) < RolloutPercent,
        };
    }
}

public static class KnownFeatureFlags
{
    /// <summary>Flags of §18 with their MVP-0 default (only the control cohort is on).</summary>
    public static IReadOnlyList<FeatureFlag> Defaults { get; } =
    [
        .. new[]
        {
            "car_mode", "background_discovery", "automatic_audio", "recommendations_cf", "surprise_me",
            "offline_packs", "anecdotes", "english", "ads", "kyutai_tts",
        }.Select(name => new FeatureFlag(name, false, 0, [], null)),
        new FeatureFlag("control_cohort", true, 100, [], null),
    ];
}
