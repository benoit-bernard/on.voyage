using System.Text.Json;

namespace OnVoyage.Platform.Contracts;

// Integration events (cahier des charges §13). Immutable, versioned by name; consumers are idempotent (EventId, Version).

public sealed record ConfigChangedV1(Guid EventId, DateTimeOffset OccurredAt, string Key, string ValueJson, int Version);

public sealed record ConsentChangedV1(Guid EventId, DateTimeOffset OccurredAt, Guid TravelerId, string Kind, bool Granted, string TextVersion);

// Public DTOs.

public sealed record ClientConfigDto(string Revision, IReadOnlyDictionary<string, JsonElement> Config, IReadOnlyDictionary<string, bool> Flags);

public sealed record ConfigEntryDto(string Key, JsonElement Value, int Version, string UpdatedBy, DateTimeOffset UpdatedAt);

public sealed record SetConfigRequest(JsonElement Value);

public sealed record FeatureFlagDto(string Name, bool Enabled, int RolloutPercent, IReadOnlyList<string> Platforms, string? MinAppVersion);

public sealed record SetFeatureFlagRequest(bool Enabled, int RolloutPercent, IReadOnlyList<string>? Platforms, string? MinAppVersion);

public sealed record ConsentDto(string Kind, bool Granted, string TextVersion, DateTimeOffset UpdatedAt);

public sealed record SetConsentRequest(bool Granted, string TextVersion);
