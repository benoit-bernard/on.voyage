using System.Text.Json;

namespace OnVoyage.Platform.Contracts;

// Integration events (cahier des charges §13). Immutable, versioned by name; consumers are idempotent (EventId, Version).

public sealed record ConfigChangedV1(Guid EventId, DateTimeOffset OccurredAt, string Key, string ValueJson, int Version);

/// <summary>Every service that exposes admin writes publishes one per write; Platform keeps the consultable journal (SEC-10).</summary>
public sealed record AdminActionRecordedV1(Guid EventId, DateTimeOffset OccurredAt, string Service, string Actor, string Action, string Target, int Status, string? Summary);

public sealed record AdminActionDto(Guid EventId, DateTimeOffset At, string Service, string Actor, string Action, string Target, int Status, string? Summary);

public sealed record ConsentChangedV1(Guid EventId, DateTimeOffset OccurredAt, Guid TravelerId, string Kind, bool Granted, string TextVersion);

// Public DTOs.

public sealed record ClientConfigDto(string Revision, IReadOnlyDictionary<string, JsonElement> Config, IReadOnlyDictionary<string, bool> Flags);

public sealed record ConfigEntryDto(string Key, JsonElement Value, int Version, string UpdatedBy, DateTimeOffset UpdatedAt);

public sealed record SetConfigRequest(JsonElement Value);

public sealed record FeatureFlagDto(string Name, bool Enabled, int RolloutPercent, IReadOnlyList<string> Platforms, string? MinAppVersion);

public sealed record SetFeatureFlagRequest(bool Enabled, int RolloutPercent, IReadOnlyList<string>? Platforms, string? MinAppVersion);

public sealed record ConsentDto(string Kind, bool Granted, string TextVersion, DateTimeOffset UpdatedAt);

public sealed record SetConsentRequest(bool Granted, string TextVersion);

// Identity (F-01).

public sealed record AuthSessionDto(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid TravelerId,
    bool IsAnonymous,
    string? Email,
    IReadOnlyList<string> Roles);

public sealed record RefreshSessionRequest(string RefreshToken);

public sealed record RequestOtpRequest(string Email);

public sealed record VerifyOtpRequest(string Email, string Code);

public sealed record AccountDto(Guid TravelerId, bool IsAnonymous, string? Email, IReadOnlyList<string> Roles, DateTimeOffset CreatedAt);

// Deletion and export of a traveler's data (§13, T-507). Platform asks every service; each answers with the matching event.

public sealed record TravelerDeletionRequestedV1(Guid EventId, DateTimeOffset OccurredAt, Guid TravelerId, DateTimeOffset RequestedAt);

public sealed record TravelerDataDeletedV1(Guid EventId, DateTimeOffset OccurredAt, Guid TravelerId, string Service);

public sealed record TravelerExportRequestedV1(Guid EventId, DateTimeOffset OccurredAt, Guid ExportId, Guid TravelerId);

/// <summary><c>Path</c> is the JSON part's path in the private <c>exports/</c> bucket; Platform assembles the archive from the parts.</summary>
public sealed record TravelerExportPartReadyV1(Guid EventId, DateTimeOffset OccurredAt, Guid ExportId, string Service, string Path);
